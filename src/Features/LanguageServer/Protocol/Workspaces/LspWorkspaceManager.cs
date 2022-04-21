// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security.Policy;
using System.Threading;
using Microsoft.CodeAnalysis.Elfie.Diagnostics;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.LanguageServer.Handler.DocumentChanges;
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
    private readonly Dictionary<Workspace, CachedSolution?> _cachedLspSolutions = new();

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
        ClearCachedLspSolutions();
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
        ClearCachedLspSolutions();

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
        ClearCachedLspSolutions();
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
        var updatedSolutions = ComputeLspSolutions();

        var hostWorkspaceSolution = updatedSolutions.FirstOrDefault(s => s.Workspace.Kind == _hostWorkspaceKind);
        return hostWorkspaceSolution;
    }

    /// <summary>
    /// Returns a document with the LSP tracked text forked from the appropriate workspace solution.
    /// </summary>
    public Document? GetLspDocument(TextDocumentIdentifier textDocumentIdentifier)
    {
        // Ensure we have the latest lsp solutions
        var currentLspSolutions = ComputeLspSolutions();

        var uri = textDocumentIdentifier.Uri;

        // Search through the latest lsp solutions to find the document with matching uri and client name.
        var findDocumentResult = FindDocuments(uri, currentLspSolutions, _requestTelemetryLogger, _logger);

        Document? document;
        if (findDocumentResult.IsEmpty)
        {
            // We didn't find a document, if the document is open, return one from our loose files workspace.
            document = _trackedDocuments.ContainsKey(uri) ? _lspMiscellaneousFilesWorkspace?.AddMiscellaneousDocument(uri, _trackedDocuments[uri]) : null;
        }
        else
        {
            // Filter the matching documents by project context.
            document = findDocumentResult.FindDocumentInProjectContext(textDocumentIdentifier);
        }

        // We found a document in a normal workspace.  If so we can remove from our loose files workspace.
        if (document != null && document.Project.Solution.Workspace is not LspMiscellaneousFilesWorkspace)
        {
            _lspMiscellaneousFilesWorkspace?.TryRemoveMiscellaneousDocument(textDocumentIdentifier.Uri);
        }

        return document;
    }

    #endregion

    /// <summary>
    /// Helper to clear out the cached LSP solutions when the LSP text changes.
    /// </summary>
    private void ClearCachedLspSolutions()
    {
        var workspaces = _cachedLspSolutions.Keys.ToImmutableArray();
        foreach (var workspace in workspaces)
        {
            _cachedLspSolutions[workspace] = null;
        }
    }

    /// <summary>
    /// Helper to get LSP solutions for all the registered workspaces.
    /// If the cached lsp solution is missing, this will retrieve the updated workspace solution (if LSP text matches)
    /// or will re-fork from the workspace (if the LSP text does not match).
    /// </summary>
    private ImmutableArray<Solution> ComputeLspSolutions()
    {
        var workspacePairs = _cachedLspSolutions.ToImmutableArray();
        using var updatedSolutions = TemporaryArray<Solution>.Empty;

        // Get the latest registered workspaces.
        var registeredWorkspaces = _lspWorkspaceRegistrationService.GetAllRegistrations();
        foreach (var workspace in registeredWorkspaces)
        {
            // Get the cached solution for this workspace if we have one.
            _cachedLspSolutions.TryGetValue(workspace, out var cachedSolution);

            if (cachedSolution == null || cachedSolution.WorkspaceVersion != workspace.CurrentSolution.WorkspaceVersion)
            {
                // We don't have a cached solution or the workspace's current solution has moved on from when we last checked.
                // We need to get the new workspace solution and re-use (if the LSP doc text matches) or fork from it.
                var workspaceSolution = workspace.CurrentSolution;
                var newSolution = GetSolutionWithReplacedDocuments(workspaceSolution, _trackedDocuments.Select(k => (k.Key, k.Value)).ToImmutableArray());

                // Store this as our most recent LSP solution along with the workspace current solution version it came from.
                _cachedLspSolutions[workspace] = new(workspaceSolution.WorkspaceVersion, newSolution);
                updatedSolutions.Add(newSolution);
            }
            else
            {
                updatedSolutions.Add(cachedSolution.CachedLspSolution);
            }
        }

        return updatedSolutions.ToImmutableAndClear();
    }

    /// <summary>
    /// Looks for document(s - e.g. linked docs) from a single solution matching the input URI in the set of passed in solutions.
    /// </summary>
    private static ImmutableArray<Document> FindDocuments(
        Uri uri,
        ImmutableArray<Solution> registeredSolutions,
        RequestTelemetryLogger telemetryLogger,
        ILspLogger logger)
    {
        logger.TraceInformation($"Finding document corresponding to {uri}");

        // Ensure we search the lsp misc files solution last if it is present.
        registeredSolutions = registeredSolutions
            .Where(solution => solution.Workspace is not LspMiscellaneousFilesWorkspace)
            .Concat(registeredSolutions.Where(solution => solution.Workspace is LspMiscellaneousFilesWorkspace)).ToImmutableArray();

        // First search the registered workspaces for documents with a matching URI.
        if (TryGetDocumentsForUri(uri, registeredSolutions, out var documents, out var solution))
        {
            telemetryLogger.UpdateFindDocumentTelemetryData(success: true, solution.Workspace.Kind);
            logger.TraceInformation($"{documents.Value.First().FilePath} found in workspace {solution.Workspace.Kind}");

            return documents.Value;
        }

        // We didn't find the document in any workspace, record a telemetry notification that we did not find it.
        var searchedWorkspaceKinds = string.Join(";", registeredSolutions.SelectAsArray(s => s.Workspace.Kind));
        logger.TraceError($"Could not find '{uri}'.  Searched {searchedWorkspaceKinds}");
        telemetryLogger.UpdateFindDocumentTelemetryData(success: false, workspaceKind: null);

        return ImmutableArray<Document>.Empty;

        static bool TryGetDocumentsForUri(
            Uri uri,
            ImmutableArray<Solution> registeredSolutions,
            [NotNullWhen(true)] out ImmutableArray<Document>? documents,
            [NotNullWhen(true)] out Solution? solution)
        {
            foreach (var registeredSolution in registeredSolutions)
            {
                var matchingDocuments = registeredSolution.GetDocuments(uri);
                if (matchingDocuments.Any())
                {
                    documents = matchingDocuments;
                    solution = registeredSolution;
                    return true;
                }
            }

            documents = null;
            solution = null;
            return false;
        }
    }

    /// <summary>
    /// Gets a solution that represents the workspace view of the world (as passed in via the solution parameter)
    /// but with document text for any open documents updated to match the LSP view of the world (if different). This makes
    /// the LSP server the source of truth for all document text, but all other changes come from the workspace
    /// </summary>
    private Solution GetSolutionWithReplacedDocuments(Solution workspaceSolution, ImmutableArray<(Uri DocumentUri, SourceText Text)> documentsToReplace)
    {
        // If our workspace's current solution has all the same text as LSP, we can just re-use the workspace solution.
        if (DoesAllLspTextMatchSolution(documentsToReplace, workspaceSolution))
        {
            return workspaceSolution;
        }

        Solution? previousWorkspaceSolution;
        lock (_gate)
        {
            _workspaceToPreviousSolution.TryGetValue(workspaceSolution.Workspace, out previousWorkspaceSolution);
        }

        // If the previous workspace current solution has all the same text as LSP, we can re-use that solution.
        if (previousWorkspaceSolution != null && DoesAllLspTextMatchSolution(documentsToReplace, previousWorkspaceSolution))
        {
            // All the LSP documents that are a part of this solution match the previous solution for this workspace.
            return previousWorkspaceSolution;
        }

        _logger.TraceWarning($"Workspace {workspaceSolution.Workspace.Kind} text did not match LSP text");

        // Neither the workspace's current solution or the previous solution matches the LSP text we have.
        // This means we need to fork from the workspace current solution and apply the LSP text on top of it.
        var forkedSolution = ForkSolution(workspaceSolution, documentsToReplace);
        return forkedSolution;

        static Solution ForkSolution(Solution workspaceSolution, ImmutableArray<(Uri DocumentUri, SourceText Text)> documentsToReplace)
        {
            var forkedSolution = workspaceSolution;
            foreach (var (uri, text) in documentsToReplace)
            {
                var documentIds = forkedSolution.GetDocumentIds(uri);
                if (!documentIds.Any())
                {
                    continue;
                }

                forkedSolution = forkedSolution.WithDocumentText(documentIds, text);
            }

            return forkedSolution;
        }
    }

    private bool DoesAllLspTextMatchSolution(ImmutableArray<(Uri DocumentUri, SourceText Text)> documentsToReplace, Solution solution)
    {
        foreach (var (uri, text) in documentsToReplace)
        {
            var documentIds = solution.GetDocumentIds(uri);
            if (!documentIds.Any())
            {
                // The document is not part of the solution.
                continue;
            }

            if (!DoesTextMatchSolution(documentIds, text, solution))
            {
                _logger.TraceInformation($"Text mismatch in workspace {solution.Workspace.Kind} for {uri}");
                return false;
            }
        }

        return true;
    }

    private static bool DoesTextMatchSolution(ImmutableArray<DocumentId> documentIds, SourceText lspText, Solution solution)
    {
        // We just want the text, so pick any of the documentIds to get the text for the document.
        var workspaceDocument = solution.GetRequiredDocument(documentIds.First());
        var workspaceText = workspaceDocument.GetTextSynchronously(CancellationToken.None);
        var workspaceChecksum = workspaceText.GetChecksum();

        var lspChecksum = lspText.GetChecksum();
        return lspChecksum.SequenceEqual(workspaceChecksum);
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
