// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.Handler.Commands;
using Microsoft.CodeAnalysis.LanguageServer.HostWorkspace;
using Roslyn.LanguageServer.Protocol;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler;

/// <summary>
/// Given an input project (or none), runs restore on the project and streams the output
/// back to the client to display.
/// </summary>
[ExportCSharpVisualBasicStatelessLspService(typeof(RestoreHandler)), Shared]
[Command(ServerRestoreCommand)]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class RestoreHandler(RestoreHelper restoreHelper) : AbstractExecuteWorkspaceCommandHandler
{
    internal const string ServerRestoreCommand = "dotnet.server.restore";

    public override bool MutatesSolutionState => false;

    public override bool RequiresLSPSolution => true;

    public override string Command => ServerRestoreCommand;

    public override async Task<object> HandleRequestAsync(ExecuteCommandParams request, RequestContext context, CancellationToken cancellationToken)
    {
        Contract.ThrowIfNull(context.Solution, "Solution cannot be null");

        var restoreParams = request.Arguments?.FirstOrDefault() as RestoreParams;
        Contract.ThrowIfNull(restoreParams, "Restore command arguments are not correct");

        var restorePaths = GetRestorePaths(restoreParams, context.Solution, context);
        if (restorePaths.IsEmpty)
        {
            context.TraceInformation($"Restore was requested but no paths were provided.");
            return new object();
        }

        var languageServerManager = context.GetRequiredLspService<IClientLanguageServerManager>();
        await restoreHelper.RestoreWithWorkDoneProgressAsync(restorePaths, languageServerManager, cancellationToken);

        return new object();
    }

    private static ImmutableArray<string> GetRestorePaths(RestoreParams request, Solution solution, RequestContext context)
    {
        if (request.ProjectFilePaths.Any())
        {
            return [.. request.ProjectFilePaths];
        }

        // No file paths were specified - this means we should restore all projects in the solution.
        // If there is a valid solution path, use that as the restore path.
        if (solution.FilePath != null)
        {
            return [solution.FilePath];
        }

        // We don't have an addressable solution, so lets find all addressable projects.
        // We can only restore projects with file paths as we are using the dotnet CLI to address them.
        // We also need to remove duplicates as in multi targeting scenarios there will be multiple projects with the same file path.
        var projects = solution.Projects
            .Select(p => p.FilePath)
            .WhereNotNull()
            .Distinct()
            .ToImmutableArray();

        context.TraceDebug($"Found {projects.Length} restorable projects from {solution.Projects.Count()} projects in solution");
        return projects;
    }

    public override TextDocumentIdentifier GetTextDocumentIdentifier(ExecuteCommandParams request)
    {
        throw new NotImplementedException();
    }
}
