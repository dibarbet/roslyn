// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.ComponentModel.Composition;
using Microsoft.CodeAnalysis.LanguageServer.Telemetry;
using Microsoft.CodeAnalysis.Remote.ProjectSystem;
using Microsoft.Extensions.Logging;
using Microsoft.ServiceHub.Framework;
using Microsoft.VisualStudio.Shell.ServiceBroker;

namespace Microsoft.CodeAnalysis.LanguageServer.HostWorkspace;

#pragma warning disable RS0030 // This is intentionally using System.ComponentModel.Composition for compatibility with MEF service broker.

/// <summary>
/// An implementation of the brokered service <see cref="IWorkspaceProjectFactoryService"/> that just maps calls to the underlying project system.
/// </summary>
internal sealed class WorkspaceProjectFactoryService(
    LanguageServerWorkspaceFactory workspaceFactory,
    ProjectInitializationStatusSubscriber projectInitializationStatusSubscriber,
    ILoggerFactory loggerFactory) : IWorkspaceProjectFactoryService, IExportedBrokeredService
{
    private readonly LanguageServerWorkspaceFactory _workspaceFactory = workspaceFactory;
    private readonly ProjectInitializationStatusSubscriber _projectInitializationStatusSubscriber = projectInitializationStatusSubscriber;
    private readonly ILogger _logger = loggerFactory.CreateLogger(nameof(WorkspaceProjectFactoryService));
    private readonly ILoggerFactory _loggerFactory = loggerFactory;

    private Task? _initializationTask;

    ServiceRpcDescriptor IExportedBrokeredService.Descriptor => WorkspaceProjectFactoryServiceDescriptor.ServiceDescriptor;

    Task IExportedBrokeredService.InitializeAsync(CancellationToken cancellationToken)
        => _initializationTask ??= _projectInitializationStatusSubscriber.SubscribeToInitializationCompleteAsync(cancellationToken);

    public async Task<IWorkspaceProject> CreateAndAddProjectAsync(WorkspaceProjectCreationInfo creationInfo, CancellationToken _)
    {
        _logger.LogInformation(string.Format(LanguageServerResources.Project_0_loaded_by_CSharp_Dev_Kit, creationInfo.FilePath));
        VSCodeRequestTelemetryLogger.ReportProjectLoadStarted();
        try
        {
            if (creationInfo.BuildSystemProperties.TryGetValue("SolutionPath", out var solutionPath))
            {
                _workspaceFactory.HostProjectFactory.SolutionPath = solutionPath;
            }

            var project = await _workspaceFactory.HostProjectFactory.CreateAndAddToWorkspaceAsync(
                creationInfo.DisplayName,
                creationInfo.Language,
                new Workspaces.ProjectSystem.ProjectSystemProjectCreationInfo { FilePath = creationInfo.FilePath },
                _workspaceFactory.ProjectSystemHostInfo).ConfigureAwait(false);

            var workspaceProject = new WorkspaceProject(project, _workspaceFactory.HostWorkspace.Services.SolutionServices, _workspaceFactory.TargetFrameworkManager, _loggerFactory);

            await workspaceProject.SetBuildSystemPropertiesAsync(creationInfo.BuildSystemProperties, CancellationToken.None).ConfigureAwait(false);

            return workspaceProject;
        }
        catch (Exception e) when (LanguageServerFatalError.ReportAndLogAndPropagate(e, _logger, $"Failed to create project {creationInfo.DisplayName}"))
        {
            throw ExceptionUtilities.Unreachable();
        }
    }

    public Task<IReadOnlyCollection<string>> GetSupportedBuildSystemPropertiesAsync(CancellationToken _)
        => Task.FromResult<IReadOnlyCollection<string>>([]);
}
#pragma warning restore RS0030 // Do not used banned APIs
