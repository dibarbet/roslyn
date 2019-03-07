// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.CommentSelection;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Text.Shared.Extensions;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;
using Roslyn.Utilities;
using VSCommanding = Microsoft.VisualStudio.Commanding;

namespace Microsoft.CodeAnalysis.Editor.Implementation.CommentSelection
{
    [Export(typeof(VSCommanding.ICommandHandler))]
    [ContentType(ContentTypeNames.RoslynContentType)]
    [Name(PredefinedCommandHandlerNames.CommentSelection)]
    internal class ToggleBlockCommentCommandHandler :
        VSCommanding.ICommandHandler<CommentSelectionCommandArgs>,
        VSCommanding.ICommandHandler<UncommentSelectionCommandArgs>
    {
        private readonly ITextUndoHistoryRegistry _undoHistoryRegistry;
        private readonly IEditorOperationsFactoryService _editorOperationsFactoryService;

        [ImportingConstructor]
        internal ToggleBlockCommentCommandHandler(
            ITextUndoHistoryRegistry undoHistoryRegistry,
            IEditorOperationsFactoryService editorOperationsFactoryService)
        {
            Contract.ThrowIfNull(undoHistoryRegistry);
            Contract.ThrowIfNull(editorOperationsFactoryService);

            _undoHistoryRegistry = undoHistoryRegistry;
            _editorOperationsFactoryService = editorOperationsFactoryService;
        }

        public string DisplayName => EditorFeaturesResources.Comment_Uncomment_Selection;

        private static VSCommanding.CommandState GetCommandState(ITextBuffer buffer)
        {
            if (!buffer.CanApplyChangeDocumentToWorkspace())
            {
                return VSCommanding.CommandState.Unspecified;
            }

            return VSCommanding.CommandState.Available;
        }

        public VSCommanding.CommandState GetCommandState(CommentSelectionCommandArgs args)
        {
            return GetCommandState(args.SubjectBuffer);
        }

        /// <summary>
        /// Comment the selected spans, and reset the selection.
        /// </summary>
        public bool ExecuteCommand(CommentSelectionCommandArgs args, CommandExecutionContext context)
        {
            return this.ExecuteCommand(args.TextView, args.SubjectBuffer, context);
        }

        public VSCommanding.CommandState GetCommandState(UncommentSelectionCommandArgs args)
        {
            return GetCommandState(args.SubjectBuffer);
        }

        /// <summary>
        /// Uncomment the selected spans, and reset the selection.
        /// </summary>
        public bool ExecuteCommand(UncommentSelectionCommandArgs args, CommandExecutionContext context)
        {
            return this.ExecuteCommand(args.TextView, args.SubjectBuffer, context);
        }

        internal bool ExecuteCommand(ITextView textView, ITextBuffer subjectBuffer, CommandExecutionContext context)
        {
            var title = EditorFeaturesResources.Comment_Selection;

            var message = EditorFeaturesResources.Commenting_currently_selected_text;

            using (context.OperationContext.AddScope(allowCancellation: false, message))
            {
                var document = subjectBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges();
                if (document == null)
                {
                    return true;
                }

                var service = document.GetLanguageService<ICommentSelectionService>();
                if (service == null)
                {
                    return true;
                }

                var trackingSpans = new Dictionary<ITrackingSpan, Operation>();
                var textChanges = new List<TextChange>();
                CollectEdits(
                    document, service, textView.Selection.GetSnapshotSpansOnBuffer(subjectBuffer),
                    textChanges, trackingSpans, CancellationToken.None);

                using (var transaction = new CaretPreservingEditTransaction(title, textView, _undoHistoryRegistry, _editorOperationsFactoryService))
                {
                    document.Project.Solution.Workspace.ApplyTextChanges(document.Id, textChanges, CancellationToken.None);
                    transaction.Complete();
                }

                // Format if result is uncomment (move)
                using (var transaction = new CaretPreservingEditTransaction(title, textView, _undoHistoryRegistry, _editorOperationsFactoryService))
                {
                    Format(service, subjectBuffer.CurrentSnapshot, trackingSpans, CancellationToken.None);
                    transaction.Complete();
                }

                if (trackingSpans.Any())
                {
                    // TODO, this doesn't currently handle block selection
                    textView.SetSelection(trackingSpans.First().Key.GetSpan(subjectBuffer.CurrentSnapshot));
                }
            }

            return true;
        }

        private static void Format(ICommentSelectionService service, ITextSnapshot snapshot, IDictionary<ITrackingSpan, Operation> changes, CancellationToken cancellationToken)
        {
            var document = snapshot.GetOpenDocumentInCurrentContextWithChanges();
            if (document == null)
            {
                return;
            }

            var textSpans = changes
                .Where(change => change.Value == Operation.Uncomment)
                .Select(uncommentChange => uncommentChange.Key.GetSpan(snapshot).Span.ToTextSpan())
                .ToImmutableArray();
            var newDocument = service.FormatAsync(document, textSpans, cancellationToken).WaitAndGetResult(cancellationToken);
            newDocument.Project.Solution.Workspace.ApplyDocumentChanges(newDocument, cancellationToken);
        }

        /// <summary>
        /// Add the necessary edits to the given spans. Also collect tracking spans over each span.
        /// Internal so that it can be called by unit tests.
        /// </summary>
        internal void CollectEdits(
            Document document, ICommentSelectionService service, NormalizedSnapshotSpanCollection selectedSpans,
            List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans, CancellationToken cancellationToken)
        {
            foreach (var span in selectedSpans)
            {
                ToggleBlockComment(document, service, span, textChanges, trackingSpans, cancellationToken);
            }
        }

        private static void ToggleBlockComment(Document document, ICommentSelectionService service, SnapshotSpan selectedSpan,
            List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans, CancellationToken cancellationToken)
        {
            var commentInfo = service.GetInfoAsync(document, selectedSpan.Span.ToTextSpan(), cancellationToken).WaitAndGetResult(cancellationToken);

            var blockCommentSelection = new BlockCommentSelection(commentInfo, selectedSpan);

            if (commentInfo.SupportsBlockComment)
            {
                if (TryUncommentBlockComment(blockCommentSelection, textChanges, trackingSpans))
                {
                    return;
                }
                else
                {
                    BlockCommentSpan(blockCommentSelection, textChanges, trackingSpans);
                }
            }
        }

        private static bool TryUncommentBlockComment(BlockCommentSelection blockCommentSelection, List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans)
        {
            // If the selection is just a caret, try and uncomment blocks on the same line.
            if (blockCommentSelection.SelectedSpan.IsEmpty && TryUncommentBlockOnSameLine(blockCommentSelection, textChanges, trackingSpans))
            {
                return true;
            }

            // If there are not any block comments intersecting the selection, there is nothing to uncomment.
            if (!blockCommentSelection.HasIntersectingBlockComments())
            {
                return false;
            }

            // If the selection is entirely commented, remove the block comments that intersect.
            if (blockCommentSelection.IsEntirelyCommented())
            {
                var intersectingBlockComments = blockCommentSelection.IntersectingBlockComments;
                foreach (var spanToRemove in intersectingBlockComments)
                {
                    DeleteBlockComment(textChanges, spanToRemove, blockCommentSelection.CommentSelectionInfo);
                }
                var trackingSpan = Span.FromBounds(intersectingBlockComments.First().Start, intersectingBlockComments.Last().End);
                trackingSpans.Add(blockCommentSelection.GetTrackingSpan(trackingSpan, SpanTrackingMode.EdgeExclusive), Operation.Uncomment);
                return true;
            }
            else
            {
                // If the span intersects with any other block comments but is not entirely commented, pass to add block comment handler.
                if (blockCommentSelection.HasBlockCommentMarker())
                {
                    return false;
                }

                return TryUncommentSelectedSpanWithinBlockComment(blockCommentSelection, textChanges, trackingSpans);
            }
        }

        /// <summary>
        /// Uncomment a block comment on the same line as the caret.
        /// </summary>
        private static bool TryUncommentBlockOnSameLine(BlockCommentSelection blockCommentSelection, List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans)
        {
            if (blockCommentSelection.TryGetBlockCommentOnSameLine(out var blockCommentOnSameLine))
            {
                DeleteBlockComment(textChanges, blockCommentOnSameLine, blockCommentSelection.CommentSelectionInfo);
                trackingSpans.Add(blockCommentSelection.GetTrackingSpan(blockCommentOnSameLine, SpanTrackingMode.EdgeExclusive), Operation.Uncomment);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Check if the selection is entirely contained within a block comment and remove it.
        /// </summary>
        private static bool TryUncommentSelectedSpanWithinBlockComment(BlockCommentSelection blockCommentSelection, List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans)
        {
            if (blockCommentSelection.TryGetSurroundingBlockComment(out var containingSpan))
            {
                DeleteBlockComment(textChanges, containingSpan, blockCommentSelection.CommentSelectionInfo);
                trackingSpans.Add(blockCommentSelection.GetTrackingSpan(containingSpan, SpanTrackingMode.EdgeExclusive), Operation.Uncomment);
                return true;
            }

            return false;
        }

        private static void BlockCommentSpan(BlockCommentSelection blockCommentSelection, List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans)
        {
            if (blockCommentSelection.HasIntersectingBlockComments())
            {
                AddBlockCommentWithIntersectingSpans(blockCommentSelection, textChanges, trackingSpans);
            }
            else
            {
                AddBlockComment(blockCommentSelection.SelectedSpan, textChanges, trackingSpans, blockCommentSelection.CommentSelectionInfo);
            }
        }

        /// <summary>
        /// Adds a block comment when the selection already contains block comment(s).
        /// The result will be sequential block comments with the entire selection being commented out.
        /// </summary>
        private static void AddBlockCommentWithIntersectingSpans(BlockCommentSelection blockCommentSelection, List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans)
        {
            var info = blockCommentSelection.CommentSelectionInfo;
            var selectedSpan = blockCommentSelection.SelectedSpan;
            var spanTrackingMode = SpanTrackingMode.EdgeInclusive;

            // Add comments to all uncommented spans in the selection.
            foreach (var uncommentedSpan in blockCommentSelection.UncommentedSpansInSelection)
            {
                InsertText(textChanges, uncommentedSpan.Start, info.BlockCommentStartString);
                InsertText(textChanges, uncommentedSpan.End, info.BlockCommentEndString);
                trackingSpans.Add(blockCommentSelection.GetTrackingSpan(selectedSpan, spanTrackingMode), Operation.Comment);
            }

            // If the start is commented (and not a comment marker), close the current comment and open a new one.
            if (blockCommentSelection.IsLocationCommented(selectedSpan.Start) && !blockCommentSelection.DoesBeginWithBlockComment())
            {
                InsertText(textChanges, selectedSpan.Start, info.BlockCommentEndString);
                InsertText(textChanges, selectedSpan.Start, info.BlockCommentStartString);
            }

            // If the end is commented (and not a comment marker), close the current comment and open a new one.
            if (blockCommentSelection.IsLocationCommented(selectedSpan.End) && !blockCommentSelection.DoesEndWithBlockComment())
            {
                InsertText(textChanges, selectedSpan.End, info.BlockCommentEndString);
                InsertText(textChanges, selectedSpan.End, info.BlockCommentStartString);
            }
        }

        private static void AddBlockComment(SnapshotSpan span, List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans, CommentSelectionInfo commentInfo)
        {
            trackingSpans.Add(span.Snapshot.CreateTrackingSpan(span, SpanTrackingMode.EdgeInclusive), Operation.Comment);
            InsertText(textChanges, span.Start, commentInfo.BlockCommentStartString);
            InsertText(textChanges, span.End, commentInfo.BlockCommentEndString);
        }

        private static void DeleteBlockComment(List<TextChange> textChanges, Span spanToRemove, CommentSelectionInfo commentInfo)
        {
            DeleteText(textChanges, new TextSpan(spanToRemove.Start, commentInfo.BlockCommentStartString.Length));
            DeleteText(textChanges, new TextSpan(spanToRemove.End - commentInfo.BlockCommentEndString.Length, commentInfo.BlockCommentEndString.Length));
        }

        private static void InsertText(List<TextChange> textChanges, int position, string text)
        {
            textChanges.Add(new TextChange(new TextSpan(position, 0), text));
        }

        private static void DeleteText(List<TextChange> textChanges, TextSpan span)
        {
            textChanges.Add(new TextChange(span, string.Empty));
        }

        private class BlockCommentSelection
        {
            public string Text { get; }

            public CommentSelectionInfo CommentSelectionInfo { get; }

            public SnapshotSpan SelectedSpan { get; }

            private readonly Lazy<IEnumerable<Span>> _uncommentedSpansInSelection;
            public IEnumerable<Span> UncommentedSpansInSelection
            {
                get { return _uncommentedSpansInSelection.Value; }
            }

            private readonly Lazy<IEnumerable<Span>> _intersectingBlockComments;
            public IEnumerable<Span> IntersectingBlockComments
            {
                get { return _intersectingBlockComments.Value; }
            }

            public BlockCommentSelection(CommentSelectionInfo commentSelectionInfo, SnapshotSpan selectedSpan)
            {
                CommentSelectionInfo = commentSelectionInfo;
                SelectedSpan = selectedSpan;
                Text = selectedSpan.GetText().Trim();

                _intersectingBlockComments = new Lazy<IEnumerable<Span>>(GetIntersectingBlockComments);
                _uncommentedSpansInSelection = new Lazy<IEnumerable<Span>>(GetUncommentedSpansInSelection);
            }

            /// <summary>
            /// Gets a list of all commented spans.
            /// A commented span is defined by the first open marker until the first close marker.
            /// Once an open marker is found, subsequent open markers are ignored until a closing marker is found.
            /// </summary>
            /// <returns>a list of all block commented spans after the index, inclusive of the block comment end string.</returns>
            private IEnumerable<Span> GetIntersectingBlockComments()
            {
                var selectedSnapshot = SelectedSpan.Snapshot;
                var allText = selectedSnapshot.AsText();
                var selectedLine = selectedSnapshot.GetLineFromPosition(SelectedSpan.Start);
                var commentedSpans = new List<Span>();

                var openIdx = 0;
                while ((openIdx = allText.IndexOf(CommentSelectionInfo.BlockCommentStartString, openIdx, caseSensitive: true)) > 0)
                {
                    // Retrieve the first closing marker located after the open index.
                    var closeIdx = allText.IndexOf(CommentSelectionInfo.BlockCommentEndString, openIdx + CommentSelectionInfo.BlockCommentStartString.Length, caseSensitive: true);

                    // If an open or close marker is not found (-1) or the open marker begins after the selection, no point in continuing.
                    if (openIdx < 0 || closeIdx < 0 || openIdx > SelectedSpan.End)
                    {
                        break;
                    }

                    var blockCommentSpan = new Span(openIdx, closeIdx + CommentSelectionInfo.BlockCommentEndString.Length - openIdx);
                    // If there is an intersection with the newly found span and the selected span, add it.
                    if (HasIntersection(blockCommentSpan))
                    {
                        commentedSpans.Add(blockCommentSpan);
                    }

                    openIdx = closeIdx;
                }

                return commentedSpans;

                bool HasIntersection(Span otherSpan)
                {
                    // Spans are intersecting if 1 location is the same between them (empty spans look at the start).
                    return SelectedSpan.OverlapsWith(otherSpan) || otherSpan.Contains(SelectedSpan);
                }
            }

            /// <summary>
            /// Retrieves all non commented, non whitespace spans.
            /// </summary>
            private IEnumerable<Span> GetUncommentedSpansInSelection()
            {
                var uncommentedSpans = new List<Span>();

                // Invert the commented spans to get the uncommented spans.
                int spanStart = SelectedSpan.Start;
                foreach (var commentedSpan in IntersectingBlockComments)
                {
                    // Only add the span if the commented span starts after the current position.
                    if (commentedSpan.Start > spanStart)
                    {
                        var newSpanEnd = commentedSpan.Start;
                        var uncommentedSpan = Span.FromBounds(spanStart, newSpanEnd);
                        // Only add the span if it is not whitespace.
                        if (!IsSpanWhitespace(uncommentedSpan))
                        {
                            uncommentedSpans.Add(Span.FromBounds(spanStart, newSpanEnd));
                        }
                    }

                    // Move to the next commented span end.
                    spanStart = commentedSpan.End;
                }

                // If part of the selection is remaining, it isn't commented.  Add it if it isn't whitespace.
                if (spanStart < SelectedSpan.End)
                {
                    var uncommentedSpan = Span.FromBounds(spanStart, SelectedSpan.End);
                    if (!IsSpanWhitespace(uncommentedSpan))
                    {
                        uncommentedSpans.Add(uncommentedSpan);
                    }
                }

                return uncommentedSpans;
            }

            /// <summary>
            /// Determines if the given span is entirely whitespace.
            /// </summary>
            /// <param name="span">the span to check for whitespace.</param>
            /// <returns>true if the span is entirely whitespace.</returns>
            private bool IsSpanWhitespace(Span span)
            {
                for (var i = span.Start; i < span.End; i++)
                {
                    if (!IsLocationWhitespace(i))
                    {
                        return false;
                    }
                }

                return true;
            }

            private bool IsLocationWhitespace(int location)
            {
                var character = SelectedSpan.Snapshot.GetPoint(location).GetChar();
                return char.IsWhiteSpace(character);
            }

            /// <summary>
            /// Determines if the location falls inside a commented span.
            /// </summary>
            public bool IsLocationCommented(int location)
            {
                return IntersectingBlockComments.Contains(span => span.Contains(location));
            }

            public bool DoesBeginWithBlockComment()
            {
                return Text.StartsWith(CommentSelectionInfo.BlockCommentStartString, StringComparison.Ordinal)
                       || Text.StartsWith(CommentSelectionInfo.BlockCommentEndString, StringComparison.Ordinal);
            }

            public bool DoesEndWithBlockComment()
            {
                return Text.EndsWith(CommentSelectionInfo.BlockCommentStartString, StringComparison.Ordinal)
                       || Text.EndsWith(CommentSelectionInfo.BlockCommentEndString, StringComparison.Ordinal);
            }

            /// <summary>
            /// Checks if the selected span contains any uncommented non whitespace characters.
            /// </summary>
            public bool IsEntirelyCommented()
            {
                return UncommentedSpansInSelection.IsEmpty();
            }

            /// <summary>
            /// Returns if the selection contains a block comment marker.
            /// </summary>
            public bool HasBlockCommentMarker()
            {
                return Text.Contains(CommentSelectionInfo.BlockCommentStartString) || Text.Contains(CommentSelectionInfo.BlockCommentEndString);
            }

            /// <summary>
            /// Returns if the selection intersects with any block comments.
            /// </summary>
            public bool HasIntersectingBlockComments()
            {
                return !IntersectingBlockComments.IsEmpty();
            }

            /// <summary>
            /// Returns a tracking span associated with the selected span.
            /// </summary>
            public ITrackingSpan GetTrackingSpan(Span span, SpanTrackingMode spanTrackingMode)
            {
                return SelectedSpan.Snapshot.CreateTrackingSpan(Span.FromBounds(span.Start, span.End), spanTrackingMode);
            }

            /// <summary>
            /// Retrive the block comment entirely surrounding the selection if it exists.
            /// </summary>
            public bool TryGetSurroundingBlockComment(out Span containingSpan)
            {
                containingSpan = IntersectingBlockComments.FirstOrDefault(commentedSpan => commentedSpan.Contains(SelectedSpan));
                if (containingSpan.Start < 0 || containingSpan.End < 0)
                {
                    return false;
                }

                return true;
            }

            /// <summary>
            /// Tries to get a block comment on the same line.  There are two cases:
            ///     1.  The caret is preceding a block comment on the same line, with only whitespace before the comment.
            ///     2.  The caret is following a block comment on the same line, with only whitespace after the comment.
            /// </summary>
            public bool TryGetBlockCommentOnSameLine(out Span commentedSpanOnSameLine)
            {
                var allText = SelectedSpan.Snapshot.AsText();
                var selectedLine = SelectedSpan.Snapshot.GetLineFromPosition(SelectedSpan.Start);

                var openMarkerIndex = selectedLine.GetText().IndexOf(CommentSelectionInfo.BlockCommentStartString) + selectedLine.Start;
                var closeMarkerIndex = selectedLine.GetText().IndexOf(CommentSelectionInfo.BlockCommentEndString) + selectedLine.Start;

                if (openMarkerIndex >= selectedLine.Start && openMarkerIndex >= SelectedSpan.Start)
                {
                    // Caret precedes comment, uncomment it if it is only whitespace preceding.
                    if (IsSpanWhitespace(Span.FromBounds(selectedLine.Start, openMarkerIndex)))
                    {
                        var closeMarkerAfterOpenMarker = allText.IndexOf(CommentSelectionInfo.BlockCommentEndString, openMarkerIndex, caseSensitive: true);
                        if (closeMarkerAfterOpenMarker >= 0)
                        {
                            commentedSpanOnSameLine = Span.FromBounds(openMarkerIndex, closeMarkerAfterOpenMarker + CommentSelectionInfo.BlockCommentEndString.Length);
                            return true;
                        }
                    }
                }
                else if (closeMarkerIndex >= selectedLine.Start && closeMarkerIndex <= SelectedSpan.Start)
                {
                    // Caret is located after a comment, uncomment if only whitespace follows the comment.
                    // Don't include the end comment marker in the span to check.
                    if (IsSpanWhitespace(Span.FromBounds(closeMarkerIndex + CommentSelectionInfo.BlockCommentEndString.Length, selectedLine.End)))
                    {
                        var openMarkerBeforeCloseMarker = allText.LastIndexOf(CommentSelectionInfo.BlockCommentStartString, closeMarkerIndex, caseSensitive: true);
                        if (openMarkerBeforeCloseMarker >= 0)
                        {
                            commentedSpanOnSameLine = Span.FromBounds(openMarkerBeforeCloseMarker, closeMarkerIndex + CommentSelectionInfo.BlockCommentEndString.Length);
                            return true;
                        }
                    }
                }

                commentedSpanOnSameLine = new Span();
                return false;
            }
        }
    }
}
