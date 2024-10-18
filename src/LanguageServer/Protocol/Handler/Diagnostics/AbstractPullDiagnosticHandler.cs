// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeCleanup;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.SolutionCrawler;
using Roslyn.LanguageServer.Protocol;
using Roslyn.Utilities;
using LSP = Roslyn.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler.Diagnostics;

/// <summary>
/// Root type for both document and workspace diagnostic pull requests.
/// </summary>
/// <typeparam name="TDiagnosticsParams">The LSP input param type</typeparam>
/// <typeparam name="TReport">The LSP type that is reported via IProgress</typeparam>
/// <typeparam name="TReturn">The LSP type that is returned on completion of the request.</typeparam>
internal abstract partial class AbstractPullDiagnosticHandler<TDiagnosticsParams, TReport, TReturn>(
    IDiagnosticAnalyzerService diagnosticAnalyzerService,
    IDiagnosticsRefresher diagnosticRefresher,
    IGlobalOptionService globalOptions)
    : ILspServiceRequestHandler<TDiagnosticsParams, TReturn?>
    where TDiagnosticsParams : IPartialResultParams<TReport>
{
    /// <summary>
    /// Special value we use to designate workspace diagnostics vs document diagnostics.  Document diagnostics
    /// should always <see cref="VSInternalDiagnosticReport.Supersedes"/> a workspace diagnostic as the former are 'live'
    /// while the latter are cached and may be stale.
    /// </summary>
    protected const int WorkspaceDiagnosticIdentifier = 1;
    protected const int DocumentDiagnosticIdentifier = 2;

    private readonly IDiagnosticsRefresher _diagnosticRefresher = diagnosticRefresher;

    protected readonly IGlobalOptionService GlobalOptions = globalOptions;
    protected readonly IDiagnosticAnalyzerService DiagnosticAnalyzerService = diagnosticAnalyzerService;

    /// <summary>
    /// Cache where we store the data produced by prior requests so that they can be returned if nothing of significance 
    /// changed. The <see cref="VersionStamp"/> is produced by <see cref="Project.GetDependentVersionAsync(CancellationToken)"/> while the 
    /// <see cref="Checksum"/> is produced by <see cref="Project.GetDependentChecksumAsync(CancellationToken)"/>.  The former is faster
    /// and works well for us in the normal case.  The latter still allows us to reuse diagnostics when changes happen that
    /// update the version stamp but not the content (for example, forking LSP text).
    /// </summary>
    private readonly ConcurrentDictionary<string, VersionedPullCache<(int globalStateVersion, VersionStamp? dependentVersion), (int globalStateVersion, Checksum dependentChecksum)>> _categoryToVersionedCache = [];

    /// <summary>
    /// Cache where we store the hash of the last computed set of <see cref="DiagnosticData"/> and resultId for a particular combination of source Id and diagnostic request category.
    /// Used to avoid serializing the same diagnostic information when we detect diagnostics could have changed but resulted in no actual changes to the diagnostic data.
    /// Instead we report an unchanged result back to the client using the resultId.
    /// </summary>
    private readonly ConcurrentDictionary<(ProjectOrDocumentId Id, string HandlerName), (string ResultId, ImmutableArray<int> DiagnosticDataHashes)> _lastComputedDiagnosticHashes = [];

    protected virtual bool PotentialDuplicate => false;

    public bool MutatesSolutionState => false;
    public bool RequiresLSPSolution => true;

    /// <summary>
    /// Retrieve the previous results we reported.  Used so we can avoid resending data for unchanged files. Also
    /// used so we can report which documents were removed and can have all their diagnostics cleared.
    /// </summary>
    protected abstract ImmutableArray<PreviousPullResult>? GetPreviousResults(TDiagnosticsParams diagnosticsParams);

    /// <summary>
    /// Returns all the documents that should be processed in the desired order to process them in.
    /// </summary>
    protected abstract ValueTask<ImmutableArray<IDiagnosticSource>> GetOrderedDiagnosticSourcesAsync(
        TDiagnosticsParams diagnosticsParams, string? requestDiagnosticCategory, RequestContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the appropriate LSP type to report a new set of diagnostics and resultId.
    /// </summary>
    protected abstract TReport CreateReport(TextDocumentIdentifier identifier, LSP.Diagnostic[] diagnostics, string resultId);

    /// <summary>
    /// Creates the appropriate LSP type to report unchanged diagnostics. Can return <see langword="false"/> to
    /// indicate nothing should be reported.  This should be done for workspace requests to avoiding sending a huge
    /// amount of "nothing changed" responses for most files.
    /// </summary>
    protected abstract bool TryCreateUnchangedReport(TextDocumentIdentifier identifier, string resultId, [NotNullWhen(true)] out TReport? report);

    /// <summary>
    /// Creates the appropriate LSP type to report a removed file.
    /// </summary>
    protected abstract TReport CreateRemovedReport(TextDocumentIdentifier identifier);

    protected abstract TReturn? CreateReturn(BufferedProgress<TReport> progress);

    protected abstract string? GetRequestDiagnosticCategory(TDiagnosticsParams diagnosticsParams);

    /// <summary>
    /// Used by public workspace pull diagnostics to allow it to keep the connection open until
    /// changes occur to avoid the client spamming the server with requests.
    /// </summary>
    protected virtual Task WaitForChangesAsync(string? category, RequestContext context, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task<TReturn?> HandleRequestAsync(
        TDiagnosticsParams diagnosticsParams, RequestContext context, CancellationToken cancellationToken)
    {
        // The progress object we will stream reports to.
        using var progress = BufferedProgress.Create(diagnosticsParams.PartialResultToken);

        // We only support this option to disable crawling in internal speedometer and ddrit perf runs to lower
        // noise.  It is not exposed to the user.
        if (!this.GlobalOptions.GetOption(SolutionCrawlerRegistrationService.EnableSolutionCrawler))
        {
            context.TraceInformation($"{this.GetType()}. Skipping due to {nameof(SolutionCrawlerRegistrationService.EnableSolutionCrawler)}={false}");
        }
        else
        {
            Contract.ThrowIfNull(context.Solution);

            var clientCapabilities = context.GetRequiredClientCapabilities();
            var category = GetRequestDiagnosticCategory(diagnosticsParams);
            var handlerName = $"{this.GetType().Name}(category: {category})";
            context.TraceInformation($"{handlerName} started getting diagnostics");

            var versionedCache = _categoryToVersionedCache.GetOrAdd(handlerName, static handlerName => new(handlerName));

            // Get the set of results the request said were previously reported.  We can use this to determine both
            // what to skip, and what files we have to tell the client have been removed.
            var previousResults = GetPreviousResults(diagnosticsParams) ?? [];
            context.TraceInformation($"previousResults.Length={previousResults.Length}");

            // Create a mapping from documents to the previous results the client says it has for them.  That way as we
            // process documents we know if we should tell the client it should stay the same, or we can tell it what
            // the updated diagnostics are.
            using var _1 = PooledDictionary<ProjectOrDocumentId, PreviousPullResult>.GetInstance(out var documentIdToPreviousDiagnosticParams);
            using var _2 = PooledHashSet<PreviousPullResult>.GetInstance(out var removedDocuments);
            await ProcessPreviousResultsAsync(context.Solution, previousResults, documentIdToPreviousDiagnosticParams, removedDocuments, cancellationToken).ConfigureAwait(false);

            // First, let the client know if any workspace documents have gone away.  That way it can remove those for
            // the user from squiggles or error-list.
            HandleRemovedDocuments(context, removedDocuments, progress);

            // Next process each file in priority order. Determine if diagnostics are changed or unchanged since the
            // last time we notified the client.  Report back either to the client so they can update accordingly.
            var orderedSources = await GetOrderedDiagnosticSourcesAsync(
                diagnosticsParams, category, context, cancellationToken).ConfigureAwait(false);

            context.TraceInformation($"Processing {orderedSources.Length} documents");

            // Keep track of what diagnostic sources we see this time around.  For any we do not see this time
            // around, we'll notify the client that the diagnostics for it have been removed.
            using var _3 = PooledHashSet<ProjectOrDocumentId>.GetInstance(out var seenDiagnosticSourceIds);

            foreach (var diagnosticSource in orderedSources)
            {
                seenDiagnosticSourceIds.Add(diagnosticSource.GetId());
                var globalStateVersion = _diagnosticRefresher.GlobalStateVersion;

                var project = diagnosticSource.GetProject();

                var newResultId = await versionedCache.GetNewResultIdAsync(
                    documentIdToPreviousDiagnosticParams,
                    diagnosticSource.GetId(),
                    project,
                    computeCheapVersionAsync: async () => (globalStateVersion, await project.GetDependentVersionAsync(cancellationToken).ConfigureAwait(false)),
                    computeExpensiveVersionAsync: async () => (globalStateVersion, await project.GetDependentChecksumAsync(cancellationToken).ConfigureAwait(false)),
                    cancellationToken).ConfigureAwait(false);
                if (newResultId != null)
                {
                    // Retrieve the last resultId that the client knows about (if any) from the previous report information on the request.
                    string? lastClientResultId = null;
                    if (documentIdToPreviousDiagnosticParams.TryGetValue(diagnosticSource.GetId(), out var previousPullResult))
                    {
                        lastClientResultId = previousPullResult.PreviousResultId;
                    }

                    await ComputeAndReportCurrentDiagnosticsAsync(
                        context, diagnosticSource, progress, handlerName, newResultId, lastClientResultId, clientCapabilities, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    context.TraceInformation($"Diagnostics were unchanged for {diagnosticSource.ToDisplayString()}");

                    // Nothing changed between the last request and this one.  Report a (null-diagnostics,
                    // same-result-id) response to the client as that means they should just preserve the current
                    // diagnostics they have for this file.
                    //
                    // Note: if this is a workspace request, we can do nothing, as that will be interpreted by the
                    // client as nothing having been changed for that document.
                    var previousParams = documentIdToPreviousDiagnosticParams[diagnosticSource.GetId()];
                    if (TryCreateUnchangedReport(previousParams.TextDocument, previousParams.PreviousResultId, out var report))
                        progress.Report(report);
                }
            }

            // Now, for any diagnostics reported from a prior source that we do not see this time around, report its
            // diagnostics as being removed. This allows for different sets of diagnostic-sources to be computed
            // each time around, while still producing accurate diagnostic reports.
            //
            // Only do this if we haven't already created a removal report for that prior result above.
            //
            // Note: we are intentionally notifying the client that this is not a remove (vs an empty set of
            // results). As far as we and the client are concerned, this document no longer exists at this point
            // for the purposes of diagnostics.
            foreach (var (projectOrDocumentId, previousDiagnosticParams) in documentIdToPreviousDiagnosticParams)
            {
                if (!seenDiagnosticSourceIds.Contains(projectOrDocumentId) &&
                    !removedDocuments.Contains(previousDiagnosticParams))
                {
                    progress.Report(CreateRemovedReport(previousDiagnosticParams.TextDocument));
                }
            }

            // Clear out the solution context to avoid retaining memory
            // https://devdiv.visualstudio.com/DevDiv/_workitems/edit/1809058
            context.ClearSolutionContext();

            // Some implementations of the spec will re-open requests as soon as we close them, spamming the server.
            // In those cases, we wait for the implementation to indicate that changes have occurred, then we close the connection
            // so that the client asks us again.
            await WaitForChangesAsync(category, context, cancellationToken).ConfigureAwait(false);

            // If we had a progress object, then we will have been reporting to that.  Otherwise, take what we've been
            // collecting and return that.
            context.TraceInformation($"{this.GetType()} finished getting diagnostics");
        }

        return CreateReturn(progress);

        static async Task ProcessPreviousResultsAsync(
            Solution solution,
            ImmutableArray<PreviousPullResult> previousResults,
            Dictionary<ProjectOrDocumentId, PreviousPullResult> idToPreviousDiagnosticParams,
            HashSet<PreviousPullResult> removedResults,
            CancellationToken cancellationToken)
        {
            foreach (var diagnosticParams in previousResults)
            {
                if (diagnosticParams.TextDocument != null)
                {
                    var id = await GetIdForPreviousResultAsync(diagnosticParams.TextDocument, solution, cancellationToken).ConfigureAwait(false);
                    if (id != null)
                    {
                        idToPreviousDiagnosticParams[id.Value] = diagnosticParams;
                    }
                    else
                    {
                        // The client previously had a result from us for this document, but we no longer have it in our solution.
                        // Record it so we can report to the client that it has been removed.
                        removedResults.Add(diagnosticParams);
                    }
                }
            }
        }

        static async Task<ProjectOrDocumentId?> GetIdForPreviousResultAsync(TextDocumentIdentifier textDocumentIdentifier, Solution solution, CancellationToken cancellationToken)
        {
            var document = await solution.GetTextDocumentAsync(textDocumentIdentifier, cancellationToken).ConfigureAwait(false);
            if (document != null)
            {
                return new ProjectOrDocumentId(document.Id);
            }

            var project = solution.GetProject(textDocumentIdentifier);
            if (project != null)
            {
                return new ProjectOrDocumentId(project.Id);
            }

            var additionalDocument = solution.GetAdditionalDocument(textDocumentIdentifier);
            if (additionalDocument != null)
            {
                return new ProjectOrDocumentId(additionalDocument.Id);
            }

            return null;
        }
    }

    private bool DoAllDiagnosticsMatchLastReported(
        ImmutableArray<int> newSortedDiagnosticHashes,
        (ProjectOrDocumentId, string) key,
        string clientResultId)
    {
        if (!_lastComputedDiagnosticHashes.TryGetValue(key, out var lastComputedDiagnosticInfo))
        {
            // We don't have any cached diagnostic information from the last request on this id, nothing to compare.
            return false;
        }

        if (clientResultId != lastComputedDiagnosticInfo.ResultId)
        {
            // The client does not have the same resultId that we do in our cache, meaning they have different diagnostics.
            // We cannot re-use the data in our cache.
            return false;
        }

        // The caller sorts Always compare and store the sorted DiagnosticData hashes, so we only need to compare sequence equal.
        if (!lastComputedDiagnosticInfo.DiagnosticDataHashes.SequenceEqual(newSortedDiagnosticHashes))
        {
            // The last set of DiagnosticData hashes does not match what we just calculated.
            return false;
        }

        // The DiagnosticData hashes from the last report match what we just calculated and we have the same resultId that the client has.
        // This means we can just report the same resultId back to avoid serializing the same diagnostic information over again.
        return true;
    }

    private async Task ComputeAndReportCurrentDiagnosticsAsync(
        RequestContext context,
        IDiagnosticSource diagnosticSource,
        BufferedProgress<TReport> progress,
        string handlerName,
        string newResultId,
        string? lastClientResultId,
        ClientCapabilities clientCapabilities,
        CancellationToken cancellationToken)
    {
        using var _ = ArrayBuilder<LSP.Diagnostic>.GetInstance(out var result);
        var diagnostics = await diagnosticSource.GetDiagnosticsAsync(context, cancellationToken).ConfigureAwait(false);

        // If we can't get a text document identifier we can't report diagnostics for this source.
        // This can happen for 'fake' projects (e.g. used for TS script blocks).
        var documentIdentifier = diagnosticSource.GetDocumentIdentifier();
        if (documentIdentifier == null)
        {
            // We are not expecting to get any diagnostics for sources that don't have a path.
            Contract.ThrowIfFalse(diagnostics.IsEmpty);
            return;
        }

        context.TraceInformation($"Found {diagnostics.Length} diagnostics for {diagnosticSource.ToDisplayString()}");

        // Check if we can re-use a last reported
        var sortedDiagnosticHashes = diagnostics.SelectAsArray(d => d.GetHashCode()).Sort();
        if (lastClientResultId is not null && DoAllDiagnosticsMatchLastReported(sortedDiagnosticHashes, (diagnosticSource.GetId(), handlerName), lastClientResultId))
        {
            // TODO !!!!!
            // will never save the lastresultid in the version cache - meaning if we get the following order of changes
            // 1.  change that changes diagnostics -> recomputes
            // 2.  change that causes recompute, but diagnostics same -> recomputes, comes here and reports resultId from 1), but version cache has version 2
            // 3.  All subsequent polling passes us resultId from 1, but we have 2 saved in new version, so always recomputes and goes here.

            // Maybe have a Lazy<hash> in the resultId cache - so we can save the resultId but defer computation to outside the semaphore?
            // but still need to update the resultId if we re-used previous... so might just need two step computation.
            context.TraceInformation($"DiagnosticData matched, reusing last client resultId {lastClientResultId}");
            if (TryCreateUnchangedReport(documentIdentifier, lastClientResultId, out var unchangedReport))
                progress.Report(unchangedReport);

            return;
        }

        // The new diagnostics do not match the last reported data, we have to send new data.

        foreach (var diagnostic in diagnostics)
            result.AddRange(ConvertDiagnostic(diagnosticSource, diagnostic, clientCapabilities));

        var report = CreateReport(documentIdentifier, result.ToArray(), newResultId);
        progress.Report(report);
    }

    private void HandleRemovedDocuments(RequestContext context, HashSet<PreviousPullResult> removedPreviousResults, BufferedProgress<TReport> progress)
    {
        foreach (var removedResult in removedPreviousResults)
        {
            context.TraceInformation($"Clearing diagnostics for removed document: {removedResult.TextDocument.Uri}");

            // Client is asking server about a document that no longer exists (i.e. was removed/deleted from
            // the workspace). Report a (null-diagnostics, null-result-id) response to the client as that
            // means they should just consider the file deleted and should remove all diagnostics
            // information they've cached for it.
            progress.Report(CreateRemovedReport(removedResult.TextDocument));
        }
    }

    private ImmutableArray<LSP.Diagnostic> ConvertDiagnostic(IDiagnosticSource diagnosticSource, DiagnosticData diagnosticData, ClientCapabilities capabilities)
    {
        return ProtocolConversions.ConvertDiagnostic(
            diagnosticData,
            capabilities.HasVisualStudioLspCapability(),
            diagnosticSource.GetProject(),
            diagnosticSource.IsLiveSource(),
            PotentialDuplicate,
            GlobalOptions);
    }
}
