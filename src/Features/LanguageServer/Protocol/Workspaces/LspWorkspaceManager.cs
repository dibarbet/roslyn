// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.LanguageServer.Handler.DocumentChanges;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Collections;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Roslyn.Utilities;
using static Microsoft.CodeAnalysis.LanguageServer.Handler.RequestExecutionQueue;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// Manages the registered workspaces and corresponding LSP solutions for an LSP server.
/// This type is tied to a particular server.
/// </summary>
/// <remarks>
/// This type provides an LSP view of the registered workspace solutions so that all LSP requests operate
/// on the state of the world that matches the LSP requests we've recieved.  
/// 
/// This is done by storing the LSP text as provided by client didOpen/didClose/didChange requests.  When asked for a document we provide either
/// <list type="bullet">
///     <item> The exact workspace solution instance if all the LSP text matches what is currently in the workspace.</item>
///     <item> A fork from the workspace current solution with the LSP text applied if the LSP text does not match.  This can happen since
///     LSP text sync is asynchronous and not guaranteed to match the text in the workspace (though the majority of the time in VS it does).</item>
/// </list>
/// 
/// Doing the forking like this has a few nice properties.
/// <list type="bullet">
///   <item>99% of the time the VS workspace matches the LSP text.  In those cases we do 0 re-parsing, share compilations, versions, checksum calcs, etc.</item>
///   <item>In the 1% of the time that we do not match, we can simply and easily compute a fork.</item>
///   <item>The code is relatively straightforward</item>
/// </list>
/// </remarks>
internal class LspWorkspaceManager : IDocumentChangeTracker, IDisposable
{
    /// <summary>
    /// Associates a cached lsp solution with the workspace's current solution version that it was forked from.
    /// This allows us to re-use a cached forked solution when nothing in the workspace has changed.
    /// </summary>
    private record CachedSolution(int WorkspaceVersion, Solution CachedLspSolution);

    /// <summary>
    /// A map from workspace to the cached last calculated LSP solution for it.
    /// 
    /// When LSP text changes come in, we get a solution (either the exact workspace solution if it matches or forked from it)
    /// and cache it here so that subsequent (unchanged) requests can re-use it.
    /// 
    /// Access to this is gauranteed to be serial by the <see cref="RequestExecutionQueue"/>
    /// </summary>
    private readonly Dictionary<Workspace, CachedSolution> _cachedLspSolutions = new();

    /// <summary>
    /// Stores the current source text for each URI that is being tracked by LSP.
    /// Each time an LSP text sync notification comes in, this source text is updated to match.
    /// Used as the backing implementation for the <see cref="IDocumentChangeTracker"/>.
    /// 
    /// Note that the text here is tracked regardless of whether or not we found a matching roslyn document
    /// for the URI.
    /// 
    /// Access to this is gauranteed to be serial by the <see cref="RequestExecutionQueue"/>
    /// </summary>
    private ImmutableDictionary<Uri, SourceText> _trackedDocuments = ImmutableDictionary<Uri, SourceText>.Empty;

    /// <summary>
    /// Gate to guard access to <see cref="_workspaceToPreviousSolution"/> since we access this
    /// via workspace change events and from the LSP request queue.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// Cache to store the previous solution from the registered LSP workspace.
    /// We match the LSP text against the workspace current solution and
    /// the previous solution (the LSP text is often behind the workspace) to
    /// avoid forking solutions as much as possible for LSP requests.
    /// </summary>
    private readonly Dictionary<Workspace, Solution> _workspaceToPreviousSolution = new();

    private readonly string _hostWorkspaceKind;
    private readonly ILspLogger _logger;
    private readonly LspMiscellaneousFilesWorkspace? _lspMiscellaneousFilesWorkspace;
    private readonly LspWorkspaceRegistrationService _lspWorkspaceRegistrationService;
    private readonly RequestTelemetryLogger _requestTelemetryLogger;

    public LspWorkspaceManager(
        ILspLogger logger,
        LspMiscellaneousFilesWorkspace? lspMiscellaneousFilesWorkspace,
        LspWorkspaceRegistrationService lspWorkspaceRegistrationService,
        RequestTelemetryLogger requestTelemetryLogger)
    {
        _hostWorkspaceKind = lspWorkspaceRegistrationService.GetHostWorkspaceKind();

        _lspMiscellaneousFilesWorkspace = lspMiscellaneousFilesWorkspace;
        _logger = logger;
        _requestTelemetryLogger = requestTelemetryLogger;

        _lspWorkspaceRegistrationService = lspWorkspaceRegistrationService;
        _lspWorkspaceRegistrationService.LspSolutionChanged += OnLspWorkspaceSolutionChanged;
    }

    public void Dispose()
    {
        _cachedLspSolutions.Clear();
        _lspWorkspaceRegistrationService.LspSolutionChanged -= OnLspWorkspaceSolutionChanged;
    }

    private void OnLspWorkspaceSolutionChanged(object? sender, WorkspaceChangeEventArgs e)
    {
        // A registered workspace has changed its current solution.
        // Update our cache with the new previous workspace solution.
        lock (_gate)
        {
            _workspaceToPreviousSolution[e.OldSolution.Workspace] = e.OldSolution;
        }
    }

    #region Implementation of IDocumentChangeTracker

    /// <summary>
    /// Called by the <see cref="DidOpenHandler"/> when a document is opened in LSP.
    /// </summary>
    public void StartTracking(Uri uri, SourceText documentText)
    {
        // First, store the LSP view of the text as the uri is now owned by the LSP client.
        Contract.ThrowIfTrue(_trackedDocuments.ContainsKey(uri), $"didOpen received for {uri} which is already open.");
        _trackedDocuments = _trackedDocuments.Add(uri, documentText);

        // If LSP changed, we need to compare against the workspace again to get the updated solution.
        _cachedLspSolutions.Clear();
    }

    /// <summary>
    /// Called by the <see cref="DidCloseHandler"/> when a document is closed in LSP.
    /// </summary>
    public void StopTracking(Uri uri)
    {
        // First, stop tracking this URI and source text as it is no longer owned by LSP.
        Contract.ThrowIfFalse(_trackedDocuments.ContainsKey(uri), $"didClose received for {uri} which is not open.");
        _trackedDocuments = _trackedDocuments.Remove(uri);

        // If LSP changed, we need to compare against the workspace again to get the updated solution.
        _cachedLspSolutions.Clear();

        // Also remove it from our loose files workspace if it is still there.
        _lspMiscellaneousFilesWorkspace?.TryRemoveMiscellaneousDocument(uri);
    }

    /// <summary>
    /// Called by the <see cref="DidChangeHandler"/> when a document's text is updated in LSP.
    /// </summary>
    public void UpdateTrackedDocument(Uri uri, SourceText newSourceText)
    {
        // Store the updated LSP view of the source text.
        Contract.ThrowIfFalse(_trackedDocuments.ContainsKey(uri), $"didChange received for {uri} which is not open.");
        _trackedDocuments = _trackedDocuments.SetItem(uri, newSourceText);

        // If LSP changed, we need to compare against the workspace again to get the updated solution.
        _cachedLspSolutions.Clear();
    }

    public ImmutableDictionary<Uri, SourceText> GetTrackedLspText() => _trackedDocuments;

    #endregion

    #region LSP Solution Retrieval

    /// <summary>
    /// Returns the LSP solution associated with the workspace with the specified <see cref="_hostWorkspaceKind"/>.
    /// This is the solution used for LSP requests that pertain to the entire workspace, for example code search or workspace diagnostics.
    /// </summary>
    public Solution? TryGetHostLspSolution()
    {
        // Ensure we have the latest lsp solutions
        var updatedSolutions = GetLspSolutions();

        var hostWorkspaceSolution = updatedSolutions.FirstOrDefault(s => s.Workspace.Kind == _hostWorkspaceKind);
        return hostWorkspaceSolution;
    }

    /// <summary>
    /// Returns a document with the LSP tracked text forked from the appropriate workspace solution.
    /// </summary>
    public Document? GetLspDocument(TextDocumentIdentifier textDocumentIdentifier)
    {
        var uri = textDocumentIdentifier.Uri;

        // Get the LSP view of all the workspace solutions.
        var lspSolutions = GetLspSolutions();

        // Find the matching document from the LSP solutions.
        foreach (var lspSolution in lspSolutions)
        {
            var documents = lspSolution.GetDocuments(uri);
            if (documents.Any())
            {
                var document = documents.FindDocumentInProjectContext(textDocumentIdentifier);

                // Record telemetry that we successfully found the document.
                var workspaceKind = document.Project.Solution.Workspace.Kind;
                _requestTelemetryLogger.UpdateFindDocumentTelemetryData(success: true, workspaceKind);
                _logger.TraceInformation($"{document.FilePath} found in workspace {workspaceKind}");

                return document;
            }
        }

        // We didn't find the document in any workspace, record a telemetry notification that we did not find it.
        var searchedWorkspaceKinds = string.Join(";", lspSolutions.SelectAsArray(s => s.Workspace.Kind));
        _logger.TraceError($"Could not find '{uri}'.  Searched {searchedWorkspaceKinds}");
        _requestTelemetryLogger.UpdateFindDocumentTelemetryData(success: false, workspaceKind: null);

        // Add the document to our loose files workspace if its open.
        var miscDocument = _trackedDocuments.ContainsKey(uri) ? _lspMiscellaneousFilesWorkspace?.AddMiscellaneousDocument(uri, _trackedDocuments[uri]) : null;
        return miscDocument;
    }

    /// <summary>
    /// Gets the LSP view of all the registered workspaces' current solutions.
    /// We try to re-use the workspaces' current solutions when the LSP text matches to
    /// avoid forking whenever possible (re-use parsed trees, compilations, etc).
    /// </summary>
    /// <returns></returns>
    private ImmutableArray<Solution> GetLspSolutions()
    {
        // Ensure that the loose files workspace is searched last.
        var registeredWorkspaces = _lspWorkspaceRegistrationService.GetAllRegistrations();
        registeredWorkspaces = registeredWorkspaces
            .Where(workspace => workspace is not LspMiscellaneousFilesWorkspace)
            .Concat(registeredWorkspaces.Where(workspace => workspace is LspMiscellaneousFilesWorkspace)).ToImmutableArray();

        using var _ = ArrayBuilder<Solution>.GetInstance(out var solutions);
        foreach (var workspace in registeredWorkspaces)
        {
            var workspaceCurrentSolution = workspace.CurrentSolution;

            // Check if we have already created an LSP solution for this exact set of LSP text and this exact workspace current solution.
            if (_cachedLspSolutions.TryGetValue(workspace, out var cachedLspSolution) && cachedLspSolution.WorkspaceVersion == workspaceCurrentSolution.WorkspaceVersion)
            {
                solutions.Add(cachedLspSolution.CachedLspSolution);
            }
            else
            {
                // Cached solution does not match - get the up to date solution and cache it.
                var lspSolution = GetLspSolutionForWorkspace(workspaceCurrentSolution);
                _cachedLspSolutions[workspace] = new CachedSolution(workspaceCurrentSolution.WorkspaceVersion, lspSolution);
                solutions.Add(lspSolution);
            }
        }

        return solutions.ToImmutable();

        Solution GetLspSolutionForWorkspace(Solution workspaceCurrentSolution)
        {
            // Map the LSP text to the corresponding workspace document (if present in this workspace).
            var documentsInWorkspace = GetDocumentsForUris(_trackedDocuments.Keys.ToImmutableArray(), workspaceCurrentSolution);

            // First we check to see if the text that the workspace current solution has matches our LSP text.
            if (DoesAllTextMatchWorkspaceSolution(documentsInWorkspace))
            {
                _requestTelemetryLogger.UpdateSameAsWorkspaceSolutionTelemetryData(cacheLevel: 1);
                return workspaceCurrentSolution;
            }

            // The workspace's current solution text doesn't match, so lets see if the previous workspace solution we've cached matches.
            Solution? previousWorkspaceSolution;
            lock (_gate)
            {
                _workspaceToPreviousSolution.TryGetValue(workspaceCurrentSolution.Workspace, out previousWorkspaceSolution);
            }

            if (previousWorkspaceSolution != null && DoesAllTextMatchPreviousWorkspaceSolution(documentsInWorkspace, previousWorkspaceSolution))
            {
                _requestTelemetryLogger.UpdateSameAsWorkspaceSolutionTelemetryData(cacheLevel: 2);
                _logger.TraceInformation($"Using the cached previous solution for {workspaceCurrentSolution.Workspace.Kind}");
                return previousWorkspaceSolution;
            }

            // Neither the workspace solution or the previous workspace solution matches.
            // We must fork from the workspace and apply the LSP text on top.

            _logger.TraceWarning($"Workspace {workspaceCurrentSolution.Workspace.Kind} text did not match LSP text");
            _requestTelemetryLogger.UpdateSameAsWorkspaceSolutionTelemetryData(cacheLevel: -1);

            var lspSolution = workspaceCurrentSolution;
            foreach (var (uri, workspaceDocuments) in documentsInWorkspace)
            {
                lspSolution = lspSolution.WithDocumentText(workspaceDocuments.Select(d => d.Id), _trackedDocuments[uri]);
            }

            return lspSolution;
        }
    }

    /// <summary>
    /// Given a set of documents from the workspace's current solution, verify that the previous workspace solution
    /// has the same set of documents and that the LSP text matches what is in the previous workspace solution.
    /// </summary>
    private bool DoesAllTextMatchPreviousWorkspaceSolution(ImmutableDictionary<Uri, ImmutableArray<Document>> documentsInWorkspace, Solution previousWorkspaceSolution)
    {
        foreach (var (uriInWorkspace, workspaceDocumentsForUri) in documentsInWorkspace)
        {
            var previousWorkspaceSolutionDocumentsForUri = previousWorkspaceSolution.GetDocuments(uriInWorkspace);
            if (previousWorkspaceSolutionDocumentsForUri.Length != workspaceDocumentsForUri.Length || !previousWorkspaceSolutionDocumentsForUri.All(doc => workspaceDocumentsForUri.Any(workspaceDoc => doc.Id == workspaceDoc.Id)))
            {
                // The documents in the previous solution for the URI don't match what the current workspace solution has.
                // This means we can't re-use this solution as it is not up to date.
                _logger.TraceInformation($"Documents in previous solution for {uriInWorkspace} did not match.");
                return false;
            }

            var isTextEquivalent = AreChecksumsEqual(previousWorkspaceSolutionDocumentsForUri.First(), _trackedDocuments[uriInWorkspace]);
            if (!isTextEquivalent)
            {
                // The text does not match for this document, we cannot re-use this solution.
                _logger.TraceInformation($"Text for {uriInWorkspace} did not match text in workspace's previous solution");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Given a set of documents from the workspace current solution, verify that the LSP text for the same documents match the workspace contents.
    /// </summary>
    private bool DoesAllTextMatchWorkspaceSolution(ImmutableDictionary<Uri, ImmutableArray<Document>> documentsInWorkspace)
    {
        foreach (var (uriInWorkspace, documentsForUri) in documentsInWorkspace)
        {
            var isTextEquivalent = AreChecksumsEqual(documentsForUri.First(), _trackedDocuments[uriInWorkspace]);

            if (!isTextEquivalent)
            {
                _logger.TraceInformation($"Text for {uriInWorkspace} did not match text in workspace's current solution");
                return false;
            }
        }

        return true;
    }

    private static bool AreChecksumsEqual(Document document, SourceText lspText)
    {
        var documentText = document.GetTextSynchronously(CancellationToken.None);
        var documentTextChecksum = documentText.GetChecksum();
        var lspTextChecksum = lspText.GetChecksum();
        return lspTextChecksum.SequenceEqual(documentTextChecksum);
    }

    #endregion

    /// <summary>
    /// Using the workspace's current solutions, find the matching documents in for each URI.
    /// </summary>
    private static ImmutableDictionary<Uri, ImmutableArray<Document>> GetDocumentsForUris(ImmutableArray<Uri> trackedDocuments, Solution workspaceCurrentSolution)
    {
        using var _ = PooledDictionary<Uri, ImmutableArray<Document>>.GetInstance(out var documentsInSolution);
        foreach (var trackedDoc in trackedDocuments)
        {
            var documents = workspaceCurrentSolution.GetDocuments(trackedDoc);
            if (documents.Any())
            {
                documentsInSolution[trackedDoc] = documents;
            }
        }

        return documentsInSolution.ToImmutableDictionary();
    }

    internal TestAccessor GetTestAccessor()
            => new(this);

    internal readonly struct TestAccessor
    {
        private readonly LspWorkspaceManager _manager;

        public TestAccessor(LspWorkspaceManager manager)
            => _manager = manager;

        public LspMiscellaneousFilesWorkspace? GetLspMiscellaneousFilesWorkspace()
            => _manager._lspMiscellaneousFilesWorkspace;

        public bool IsWorkspaceRegistered(Workspace workspace)
        {
            return _manager._lspWorkspaceRegistrationService.GetAllRegistrations().Contains(workspace);
        }
    }
}
