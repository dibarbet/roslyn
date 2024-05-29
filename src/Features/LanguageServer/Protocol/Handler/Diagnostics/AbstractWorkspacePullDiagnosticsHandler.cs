// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.LanguageServer.Handler.Diagnostics.DiagnosticSources;
using Microsoft.CodeAnalysis.Options;
using Roslyn.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler.Diagnostics;

internal abstract class AbstractWorkspacePullDiagnosticsHandler<TDiagnosticsParams, TReport, TReturn>
    : AbstractPullDiagnosticHandler<TDiagnosticsParams, TReport, TReturn>, IDisposable
    where TDiagnosticsParams : IPartialResultParams<TReport>
{
    private readonly LspWorkspaceRegistrationService _workspaceRegistrationService;
    private readonly LspWorkspaceManager _workspaceManager;
    protected readonly IDiagnosticSourceManager DiagnosticSourceManager;

    /// <summary>
    /// Gate to guard access to <see cref="_changedSolutionVersion"/>
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// Stores the version stamp of the last LSP solution change that we saw
    /// We compare this against the version stamp in the request to determine
    /// if we need to close the workspace request in order to refresh diagnostics.
    /// </summary>
    /// <remarks>
    /// Note that this is initialized to a default value to differentiate
    /// </remarks>
    private VersionStamp? _changedSolutionVersion = VersionStamp.Default;

    protected AbstractWorkspacePullDiagnosticsHandler(
        LspWorkspaceManager workspaceManager,
        LspWorkspaceRegistrationService registrationService,
        IDiagnosticAnalyzerService diagnosticAnalyzerService,
        IDiagnosticSourceManager diagnosticSourceManager,
        IDiagnosticsRefresher diagnosticRefresher,
        IGlobalOptionService globalOptions) : base(diagnosticAnalyzerService, diagnosticRefresher, globalOptions)
    {
        DiagnosticSourceManager = diagnosticSourceManager;
        _workspaceManager = workspaceManager;
        _workspaceRegistrationService = registrationService;

        _workspaceRegistrationService.LspSolutionChanged += OnLspSolutionChanged;
        _workspaceManager.LspTextChanged += OnLspTextChanged;
    }

    public void Dispose()
    {
        _workspaceManager.LspTextChanged -= OnLspTextChanged;
        _workspaceRegistrationService.LspSolutionChanged -= OnLspSolutionChanged;
    }

    protected override ValueTask<ImmutableArray<IDiagnosticSource>> GetOrderedDiagnosticSourcesAsync(TDiagnosticsParams diagnosticsParams, string? requestDiagnosticCategory, RequestContext context, CancellationToken cancellationToken)
    {
        if (context.ServerKind == WellKnownLspServerKinds.RazorLspServer)
        {
            // If we're being called from razor, we do not support WorkspaceDiagnostics at all.  For razor, workspace
            // diagnostics will be handled by razor itself, which will operate by calling into Roslyn and asking for
            // document-diagnostics instead.
            return new([]);
        }

        return DiagnosticSourceManager.CreateWorkspaceDiagnosticSourcesAsync(context, requestDiagnosticCategory, cancellationToken);
    }

    private void OnLspSolutionChanged(object? sender, WorkspaceChangeEventArgs e)
    {
        UpdateLspChanged(e.NewSolution);
    }

    private void OnLspTextChanged(object? sender, EventArgs e)
    {
        // We don't have an actual solution object yet when this event is triggered,
        // but we can pass null which will trigger the request to close (and re-compute).
        //
        // Bug: unless OnLspSolutionChanged is triggered, this will lead to an infinite close/re-open loop
        // because WaitForChangesAsync will always see a null version stamp.
        UpdateLspChanged(updatedSolution: null);
    }

    private void UpdateLspChanged(Solution? updatedSolution)
    {
        lock (_gate)
        {
            _changedSolutionVersion = updatedSolution?.Version;
        }
    }

    protected override async Task WaitForChangesAsync(VersionStamp requestVersion, RequestContext context, CancellationToken cancellationToken)
    {
        // Spin waiting until our LSP change flag has been set.  When the flag is set (meaning LSP has changed),
        // we reset the flag to false and exit out of the loop allowing the request to close.
        // The client will automatically trigger a new request as soon as we close it, bringing us up to date on diagnostics.
        while (!HasChanged())
        {
            // There have been no changes between now and when the last request finished - we will hold the connection open while we poll for changes.
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }

        // We've hit a change, so we close the current request to allow the client to open a new one.
        context.TraceInformation("Closing workspace/diagnostics request");
        return;

        bool HasChanged()
        {
            lock (_gate)
            {
                var changedVersion = _changedSolutionVersion;
                if (changedVersion == null)
                {
                    // We don't have a specific last changed version, so assume it has changed.
                    return true;
                }

                var newerVersion = requestVersion.GetNewerVersion(changedVersion.Value);
                // If the request version is not the newer version stamp, things have changed and we need to re-compute.
                return newerVersion != requestVersion;
            }
        }
    }

    internal abstract TestAccessor GetTestAccessor();

    internal readonly struct TestAccessor(AbstractWorkspacePullDiagnosticsHandler<TDiagnosticsParams, TReport, TReturn> handler)
    {
        public void TriggerConnectionClose() => handler.UpdateLspChanged(null);
    }
}
