// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CommonLanguageServerProtocol.Framework;
using Roslyn.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler;

/// <summary>
/// Handles a request from the client to refresh source generators.
/// No specific generators are refreshed; rather, all generators are refreshed in all registered workspaces.
/// </summary>
[ExportCSharpVisualBasicLspService(typeof(WorkspaceRefreshSourceGeneratorsHandler)), Shared(ProtocolConstants.LspServerInstanceSharingBoundary)]
[Method(MethodName)]
internal class WorkspaceRefreshSourceGeneratorsHandler : ILspServiceNotificationHandler<RefreshSourceGeneratorsParams>, ILspService
{
    private readonly LspWorkspaceRegistrationService _workspaceRegistrationService;

    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public WorkspaceRefreshSourceGeneratorsHandler(LspServices lspServices)
        : this(lspServices.GetRequiredService<LspWorkspaceRegistrationService>())
    {
    }

    public WorkspaceRefreshSourceGeneratorsHandler(LspWorkspaceRegistrationService workspaceRegistrationService)
    {
        _workspaceRegistrationService = workspaceRegistrationService;
    }

    public const string MethodName = "workspace/_roslyn_refreshSourceGenerators";

    public bool MutatesSolutionState => false;

    public bool RequiresLSPSolution => false;

    public Task HandleNotificationAsync(RefreshSourceGeneratorsParams request, RequestContext requestContext, CancellationToken cancellationToken)
    {
        foreach (var workspace in _workspaceRegistrationService.GetAllRegistrations())
        {
            workspace.EnqueueUpdateSourceGeneratorVersion(projectId: null, request.ForceRegeneration);
        }

        return Task.CompletedTask;
    }
}

internal record RefreshSourceGeneratorsParams(
    [property: JsonPropertyName("forceRegeneration")] bool ForceRegeneration
);
