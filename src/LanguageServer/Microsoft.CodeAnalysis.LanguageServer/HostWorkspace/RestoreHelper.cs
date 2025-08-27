// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Composition;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.LanguageServer.LanguageServer;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Threading;
using NuGet.ProjectModel;
using NuGet.Versioning;
using Roslyn.LanguageServer.Protocol;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.LanguageServer.HostWorkspace;

[Export, Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal class RestoreHelper(DotnetCliHelper dotnetCliHelper, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("Restore");

    public async Task RestoreWithWorkDoneProgressAsync(ImmutableArray<string> restorePaths, IClientLanguageServerManager languageServerManager, CancellationToken cancellationToken)
    {
        if (restorePaths.IsEmpty)
        {
            return;
        }

        var progressGuid = Guid.NewGuid().ToString();
        await languageServerManager.SendRequestAsync(Methods.WindowWorkDoneProgressCreateName, new WorkDoneProgressCreateParams()
        {
            Token = progressGuid
        }, cancellationToken);

        _logger.LogInformation(LanguageServerResources.Restore_started);
        await languageServerManager.SendNotificationAsync(Methods.ProgressNotificationName, new WorkDoneProgressImpl()
        {
            Token = progressGuid,
            Value = new WorkDoneProgressBegin()
            {
                Title = LanguageServerResources.Restore,
                Cancellable = true,
                Message = LanguageServerResources.Restore_started,
                Percentage = 0
            }
        }, cancellationToken);

        _logger.LogDebug($"Running restore on {restorePaths.Length} paths, starting with '{restorePaths.First()}'.");

        var success = await RestoreAsync(restorePaths, ReportProgressAsync, dotnetCliHelper, cancellationToken);

        // TODO - handle client sending cancel.

        await languageServerManager.SendNotificationAsync(Methods.ProgressNotificationName, new WorkDoneProgressImpl()
        {
            Token = progressGuid,
            Value = new WorkDoneProgressEnd()
            {
                Message = LanguageServerResources.Restore_complete
            }
        }, cancellationToken);

        if (success)
        {
            _logger.LogInformation($"Restore completed successfully.");
        }
        else
        {
            _logger.LogError($"Restore completed with errors.");
        }

        async ValueTask ReportProgressAsync(string stage, string? restoreOutput)
        {
            if (restoreOutput != null)
            {
                _logger.LogInformation(restoreOutput);
                await languageServerManager.SendNotificationAsync(Methods.ProgressNotificationName, new WorkDoneProgressImpl()
                {
                    Token = progressGuid,
                    Value = new WorkDoneProgressReport()
                    {
                        Message = stage,
                        Percentage = null,
                        Cancellable = true
                    }
                }, cancellationToken);
            }
        }
    }

    internal static bool NeedsRestore(ProjectFileInfo newProjectFileInfo, ProjectFileInfo? previousProjectFileInfo, ILogger logger)
    {
        if (previousProjectFileInfo is null)
        {
            // This means we're likely opening the project for the first time.
            // We need to check the assets on disk to see if we need to restore.
            return CheckProjectAssetsForUnresolvedDependencies(newProjectFileInfo, logger);
        }

        var newPackageReferences = newProjectFileInfo.PackageReferences;
        var previousPackageReferences = previousProjectFileInfo.PackageReferences;

        if (newPackageReferences.Length != previousPackageReferences.Length)
        {
            // If the number of package references has changed then we need to run a restore.
            // We need to run a restore even in the removal case to ensure the items get removed from the compilation.
            return true;
        }

        if (!newPackageReferences.SetEquals(previousPackageReferences))
        {
            // The set of package references have different values.  We need to run a restore.
            return true;
        }

        // We have the same set of package references.  We still need to verify that the assets
        // exist on disk (they could have been deleted by a git clean for example).
        return CheckProjectAssetsForUnresolvedDependencies(newProjectFileInfo, logger);
    }

    private static bool CheckProjectAssetsForUnresolvedDependencies(ProjectFileInfo projectFileInfo, ILogger logger)
    {
        var projectAssetsPath = projectFileInfo.ProjectAssetsFilePath;
        if (!File.Exists(projectAssetsPath))
        {
            // If the file doesn't exist then all package references are unresolved.
            logger.LogWarning(string.Format(LanguageServerResources.Project_0_has_unresolved_dependencies, projectFileInfo.FilePath));
            return true;
        }

        if (projectFileInfo.PackageReferences.IsEmpty)
        {
            // If there are no package references then there are no unresolved dependencies.
            return false;
        }

        // Iterate the project's package references and check if there is a package with the same name
        // and acceptable version in the lock file.

        var lockFileFormat = new LockFileFormat();
        var lockFile = lockFileFormat.Read(projectAssetsPath);
        var projectAssetsMap = CreateProjectAssetsMap(lockFile);

        using var _ = PooledHashSet<PackageReference>.GetInstance(out var unresolved);

        foreach (var reference in projectFileInfo.PackageReferences)
        {
            if (!projectAssetsMap.TryGetValue(reference.Name, out var projectAssetsVersions))
            {
                // If the package name isn't in the lock file then it's unresolved.
                unresolved.Add(reference);
                continue;
            }

            var requestedVersionRange = VersionRange.TryParse(reference.VersionRange, out var versionRange)
                ? versionRange
                : VersionRange.All;

            var projectAssetsHasVersion = projectAssetsVersions.Any(projectAssetsVersion => SatisfiesVersion(requestedVersionRange, projectAssetsVersion));
            if (!projectAssetsHasVersion)
            {
                // If the package name is in the lock file but none of the versions satisfy the requested version range then it's unresolved.
                unresolved.Add(reference);
            }
        }

        if (unresolved.Any())
        {
            var message = string.Format(LanguageServerResources.Project_0_has_unresolved_dependencies, projectFileInfo.FilePath)
                + Environment.NewLine
                + string.Join(Environment.NewLine, unresolved.Select(r => $"    {r.Name}-{r.VersionRange}"));
            logger.LogWarning(message);
            return true;
        }

        return false;

        static ImmutableDictionary<string, ImmutableArray<NuGetVersion>> CreateProjectAssetsMap(LockFile lockFile)
        {
            // Create a map of package names to all versions in the lock file.
            var map = lockFile.Libraries
                .GroupBy(l => l.Name, l => l.Version, StringComparer.OrdinalIgnoreCase)
                .ToImmutableDictionary(g => g.Key, g => g.ToImmutableArray(), StringComparer.OrdinalIgnoreCase);

            return map;
        }

        static bool SatisfiesVersion(VersionRange requestedVersionRange, NuGetVersion projectAssetsVersion)
        {
            return requestedVersionRange.Satisfies(projectAssetsVersion);
        }
    }

    /// <returns>True if all restore invocations exited with code 0. Otherwise, false.</returns>
    private static async Task<bool> RestoreAsync(ImmutableArray<string> pathsToRestore, Func<string, string?, ValueTask> reportProgress, DotnetCliHelper dotnetCliHelper, CancellationToken cancellationToken)
    {
        bool success = true;
        foreach (var path in pathsToRestore)
        {
            var arguments = new string[] { "restore", path };
            var workingDirectory = Path.GetDirectoryName(path);
            var stageName = string.Format(LanguageServerResources.Restoring_0, Path.GetFileName(path));
            await reportProgress(stageName, string.Format(LanguageServerResources.Running_dotnet_restore_on_0, path));

            var process = dotnetCliHelper.Run(arguments, workingDirectory, shouldLocalizeOutput: true);

            cancellationToken.Register(() =>
            {
                process?.Kill();
            });

            process.OutputDataReceived += (sender, args) => reportProgress(stageName, args.Data);
            process.ErrorDataReceived += (sender, args) => reportProgress(stageName, args.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                await reportProgress(stageName, string.Format(LanguageServerResources.Failed_to_run_restore_on_0, path));
                success = false;
            }
        }

        return success;
    }

    private class WorkDoneProgressImpl
    {
        [JsonPropertyName("token")]
        public required string Token { get; init; }

        [JsonPropertyName("value")]
        public required WorkDoneProgress Value { get; init; }
    }
}
