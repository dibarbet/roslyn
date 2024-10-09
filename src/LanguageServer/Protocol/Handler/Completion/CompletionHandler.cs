// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.Handler.Completion;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;
using LSP = Roslyn.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler
{
    /// <summary>
    /// Handle a completion request.
    /// </summary>
    [ExportCSharpVisualBasicStatelessLspService(typeof(CompletionHandler)), Shared]
    [Method(LSP.Methods.TextDocumentCompletionName)]
    internal sealed partial class CompletionHandler : ILspServiceDocumentRequestHandler<LSP.CompletionParams, LSP.CompletionList?>
    {
        private readonly IGlobalOptionService _globalOptions;

        public bool MutatesSolutionState => false;
        public bool RequiresLSPSolution => true;

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public CompletionHandler(
            IGlobalOptionService globalOptions)
        {
            _globalOptions = globalOptions;
        }

        public LSP.TextDocumentIdentifier GetTextDocumentIdentifier(LSP.CompletionParams request) => request.TextDocument;

        public async Task<LSP.CompletionList?> HandleRequestAsync(
            LSP.CompletionParams request, RequestContext context, CancellationToken cancellationToken)
        {
            Contract.ThrowIfNull(context.Document);
            Contract.ThrowIfNull(context.Solution);

            var document = context.Document;
            var position = await document
                .GetPositionFromLinePositionAsync(ProtocolConversions.PositionToLinePosition(request.Position), cancellationToken)
                .ConfigureAwait(false);

            var capabilityHelper = new CompletionCapabilityHelper(context.GetRequiredClientCapabilities());
            var completionListCache = context.GetRequiredLspService<CompletionListCache>();

            return await GetCompletionListAsync(
                document,
                position,
                request.Context,
                _globalOptions,
                capabilityHelper,
                completionListCache,
                cancellationToken).ConfigureAwait(false);
        }

        public static async Task<LSP.CompletionList?> GetCompletionListAsync(
            Document document,
            int position,
            LSP.CompletionContext? completionContext,
            IGlobalOptionService globalOptions,
            CompletionCapabilityHelper capabilityHelper,
            CompletionListCache completionListCache,
            CancellationToken cancellationToken)
        {
            var completionOptions = globalOptions.GetCompletionOptionsForLsp(document.Project.Language, capabilityHelper);
            var completionListMaxSize = globalOptions.GetOption(LspOptionsStorage.MaxCompletionListSize);

            var documentText = await document.GetValueTextAsync(cancellationToken).ConfigureAwait(false);
            var completionTrigger = await ProtocolConversions
                .LSPToRoslynCompletionTriggerAsync(completionContext, document, position, cancellationToken)
                .ConfigureAwait(false);
            var completionService = document.GetRequiredLanguageService<CompletionService>();

            var project = document.Project;

            // Let CompletionService decide if we should trigger completion, unless the request is for incomplete results, in which case we always trigger. 
            if (completionContext?.TriggerKind is not LSP.CompletionTriggerKind.TriggerForIncompleteCompletions
                && !completionService.ShouldTriggerCompletion(project, project.Services, documentText, position, completionTrigger, completionOptions, project.Solution.Options, roles: null))
            {
                return null;
            }

            var completionListResult = await GetFilteredCompletionListAsync(
                completionContext, document, documentText, position, completionOptions, capabilityHelper, completionService, completionListCache, completionListMaxSize, cancellationToken).ConfigureAwait(false);

            if (completionListResult == null)
                return null;

            var (list, isIncomplete, isHardSelection, resultId) = completionListResult.Value;

            var result = await CompletionResultFactory
                .ConvertToLspCompletionListAsync(document, capabilityHelper, list, isIncomplete, isHardSelection, resultId, cancellationToken)
                .ConfigureAwait(false);

            return result;
        }

        private static async Task<(CompletionList CompletionList, bool IsIncomplete, bool isHardSelection, long ResultId)?> GetFilteredCompletionListAsync(
            LSP.CompletionContext? context,
            Document document,
            SourceText sourceText,
            int position,
            CompletionOptions completionOptions,
            CompletionCapabilityHelper capabilityHelper,
            CompletionService completionService,
            CompletionListCache completionListCache,
            int completionListMaxSize,
            CancellationToken cancellationToken)
        {
            var completionTrigger = await ProtocolConversions.LSPToRoslynCompletionTriggerAsync(context, document, position, cancellationToken).ConfigureAwait(false);
            var isTriggerForIncompleteCompletions = context?.TriggerKind == LSP.CompletionTriggerKind.TriggerForIncompleteCompletions;

            (CompletionList List, long ResultId)? result;
            if (isTriggerForIncompleteCompletions)
            {
                // We don't have access to the original trigger, but we know the completion list is already present.
                // It is safe to recompute with the invoked trigger as we will get all the items and filter down based on the current trigger.
                var originalTrigger = CompletionTrigger.Invoke;
                result = await CalculateListAsync(document, position, originalTrigger, completionOptions, completionService, completionListCache, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // This is a new completion request, clear out the last result Id for incomplete results.
                result = await CalculateListAsync(document, position, completionTrigger, completionOptions, completionService, completionListCache, cancellationToken).ConfigureAwait(false);
            }

            if (result == null)
            {
                return null;
            }

            var (completionList, resultId) = result.Value;

            // By default, Roslyn would treat continuous alphabetical text as a single word for completion purpose.
            // e.g. when user triggers completion at the location of {$} in "pub{$}class", the span would cover "pubclass",
            // which is used for subsequent matching and commit.
            // This works fine for VS async-completion, where we have full control of entire completion process.
            // However, the insert mode in VSCode (i.e. the mode our LSP server supports) expects us to return TextEdit that only
            // covers the span ends at the cursor location, e.g. "pub" in the example above. Here we detect when that occurs and
            // adjust the span accordingly.
            if (!capabilityHelper.SupportVSInternalClientCapabilities && position < completionList.Span.End)
            {
                var defaultSpan = new TextSpan(completionList.Span.Start, length: position - completionList.Span.Start);
                completionList = completionList.WithSpan(defaultSpan);
            }

            var (filteredCompletionList, isIncomplete, isHardSelection) = FilterCompletionList(completionList, completionListMaxSize, completionTrigger, sourceText);

            return (filteredCompletionList, isIncomplete, isHardSelection, resultId);
        }

        private static async Task<(CompletionList CompletionList, long ResultId)?> CalculateListAsync(
            Document document,
            int position,
            CompletionTrigger completionTrigger,
            CompletionOptions completionOptions,
            CompletionService completionService,
            CompletionListCache completionListCache,
            CancellationToken cancellationToken)
        {
            var completionList = await completionService.GetCompletionsAsync(document, position, completionOptions, document.Project.Solution.Options, completionTrigger, cancellationToken: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (completionList.ItemsList.IsEmpty())
            {
                return null;
            }

            // Cache the completion list so we can avoid recomputation in the resolve handler
            var resultId = completionListCache.UpdateCache(new CompletionListCache.CacheEntry(completionList));

            return (completionList, resultId);
        }

        private static (CompletionList CompletionList, bool IsIncomplete, bool isHardSelection) FilterCompletionList(
            CompletionList completionList,
            int completionListMaxSize,
            CompletionTrigger completionTrigger,
            SourceText sourceText)
        {
            var filterText = sourceText.GetSubText(completionList.Span).ToString();
            var filterReason = GetFilterReason(completionTrigger);

            // Use pattern matching to determine which items are most relevant out of the calculated items.
            using var _ = ArrayBuilder<MatchResult>.GetInstance(out var matchResultsBuilder);
            var index = 0;
            using var helper = new PatternMatchHelper(filterText);
            foreach (var item in completionList.ItemsList)
            {
                if (helper.TryCreateMatchResult(
                    item,
                    completionTrigger.Kind,
                    filterReason,
                    recentItemIndex: -1,
                    includeMatchSpans: false,
                    index,
                    out var matchResult))
                {
                    matchResultsBuilder.Add(matchResult);
                    index++;
                }
            }

            // Next, we sort the list based on the pattern matching result.
            matchResultsBuilder.Sort(MatchResult.SortingComparer);

            // TODO
            // Check if punctuation, set isIncomplete to true and set selection
            // then when client calls back we can update to hard if needed (e.g. continue typing a variable name '_' to '_otherVar'
            // can't just check soft selection for incomplete - it is possible to go from hard selection to soft (e.g. type 'a' hard selects 'async' then 'x').
            // The swap can happen multiple times, e.g. '_' (soft), '_o' (hard, assuming _otherVar), '_ox' (soft, no match).
            // 
            // For full fidelity, we can always set incomplete and have the client callback every time, but that is expensive so just check punctuation.
            var isHardSelection = true;
            if (matchResultsBuilder.Count > 0)
            {
                var bestResult = GetBestCompletionItemSelectionFromFilteredResults(matchResultsBuilder);
                isHardSelection = IsHardSelection(bestResult.CompletionItem, bestResult.ShouldBeConsideredMatchingFilterText, completionList.SuggestionModeItem != null, filterText);
            }

            // Finally, truncate the list to 1000 items plus any preselected items that occur after the first 1000.
            var filteredList = matchResultsBuilder
                .Take(completionListMaxSize)
                .Concat(matchResultsBuilder.Skip(completionListMaxSize).Where(match => match.CompletionItem.Rules.MatchPriority == MatchPriority.Preselect))
                .Select(matchResult => matchResult.CompletionItem)
                .ToImmutableArray();
            var newCompletionList = completionList.WithItemsList(filteredList);

            // Per the LSP spec, the completion list should be marked with isIncomplete = false when further insertions will
            // not generate any more completion items.  This means that we should be checking if the matchedResults is larger
            // than the filteredList.  However, the VS client has a bug where they do not properly re-trigger completion
            // when a character is deleted to go from a complete list back to an incomplete list.
            // For example, the following scenario.
            // User types "So" -> server gives subset of items for "So" with isIncomplete = true
            // User types "m" -> server gives entire set of items for "Som" with isIncomplete = false
            // User deletes "m" -> client has to remember that "So" results were incomplete and re-request if the user types something else, like "n"
            //
            // Currently the VS client does not remember to re-request, so the completion list only ever shows items from "Som"
            // so we always set the isIncomplete flag to true when the original list size (computed when no filter text was typed) is too large.
            // VS bug here - https://devdiv.visualstudio.com/DevDiv/_workitems/edit/1335142
            var isIncomplete = completionList.ItemsList.Count > newCompletionList.ItemsList.Count;

            return (newCompletionList, isIncomplete, isHardSelection);

            static CompletionFilterReason GetFilterReason(CompletionTrigger trigger)
            {
                return trigger.Kind switch
                {
                    CompletionTriggerKind.Insertion => CompletionFilterReason.Insertion,
                    CompletionTriggerKind.Deletion => CompletionFilterReason.Deletion,
                    _ => CompletionFilterReason.Other,
                };
            }
        }

        private static bool IsHardSelection(
            CompletionItem item,
            bool matchedFilterText,
            bool hasSuggestionModeItem,
            string filterText)
        {
            if (hasSuggestionModeItem)
            {
                return false;
            }

            // We don't have a builder and we have a best match.  Normally this will be hard
            // selected, except for a few cases.  Specifically, if no filter text has been
            // provided, and this is not a preselect match then we will soft select it.  This
            // happens when the completion list comes up implicitly and there is something in
            // the MRU list.  In this case we do want to select it, but not with a hard
            // selection.  Otherwise you can end up with the following problem:
            //
            //  dim i as integer =<space>
            //
            // Completion will comes up after = with 'integer' selected (Because of MRU).  We do
            // not want 'space' to commit this.

            // If all that has been typed is punctuation, then don't hard select anything.
            // It's possible the user is just typing language punctuation and selecting
            // anything in the list will interfere.  We only allow this if the filter text
            // exactly matches something in the list already. 
            if (filterText.Length > 0 && IsAllPunctuation(filterText) && filterText != item.DisplayText)
            {
                return false;
            }

            // If the user hasn't actually typed anything, then don't hard select any item.
            // The only exception to this is if the completion provider has requested the
            // item be preselected.
            if (filterText.Length == 0)
            {
                // Item didn't want to be hard selected with no filter text.
                // So definitely soft select it.
                if (item.Rules.SelectionBehavior != CompletionItemSelectionBehavior.HardSelection)
                {
                    return false;
                }

                // Item did not ask to be preselected.  So definitely soft select it.
                if (item.Rules.MatchPriority == MatchPriority.Default)
                {
                    return false;
                }
            }

            // The user typed something, or the item asked to be preselected.  In 
            // either case, don't soft select this.
            Debug.Assert(filterText.Length > 0 || item.Rules.MatchPriority != MatchPriority.Default);

            // If the user moved the caret left after they started typing, the 'best' match may not match at all
            // against the full text span that this item would be replacing.
            if (!matchedFilterText)
            {
                return false;
            }

            // There was either filter text, or this was a preselect match.  In either case, we
            // can hard select this.
            return true;
        }

        private static MatchResult GetBestCompletionItemSelectionFromFilteredResults(IReadOnlyList<MatchResult> filteredMatchResults)
        {
            Debug.Assert(filteredMatchResults.Count > 0);

            var bestResult = filteredMatchResults[0];
            var bestResultMruIndex = bestResult.RecentItemIndex;

            for (int i = 1, n = filteredMatchResults.Count; i < n; i++)
            {
                var currentResult = filteredMatchResults[i];
                var currentResultMruIndex = currentResult.RecentItemIndex;

                // Most recently used item is our top preference.
                if (currentResultMruIndex != bestResultMruIndex)
                {
                    if (currentResultMruIndex > bestResultMruIndex)
                    {
                        bestResult = currentResult;
                        bestResultMruIndex = currentResultMruIndex;
                    }

                    continue;
                }

                // 2nd preference is IntelliCode item
                var currentIsPreferred = currentResult.CompletionItem.IsPreferredItem();
                var bestIsPreferred = bestResult.CompletionItem.IsPreferredItem();

                if (currentIsPreferred != bestIsPreferred)
                {
                    if (currentIsPreferred && !bestIsPreferred)
                    {
                        bestResult = currentResult;
                    }

                    continue;
                }

                // 3rd preference is higher MatchPriority
                var currentMatchPriority = currentResult.CompletionItem.Rules.MatchPriority;
                var bestMatchPriority = bestResult.CompletionItem.Rules.MatchPriority;

                if (currentMatchPriority != bestMatchPriority)
                {
                    if (currentMatchPriority > bestMatchPriority)
                    {
                        bestResult = currentResult;
                    }

                    continue;
                }

                // final preference is match to FilterText over AdditionalFilterTexts
                if (bestResult.MatchedWithAdditionalFilterTexts && !currentResult.MatchedWithAdditionalFilterTexts)
                {
                    bestResult = currentResult;
                }
            }

            return bestResult;
        }

        private static bool IsAllPunctuation(string filterText)
        {
            foreach (var ch in filterText)
            {
                if (!char.IsPunctuation(ch))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
