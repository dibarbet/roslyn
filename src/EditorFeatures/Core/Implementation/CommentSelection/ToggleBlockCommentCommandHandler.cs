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

        private void Format(ICommentSelectionService service, ITextSnapshot snapshot, IDictionary<ITrackingSpan, Operation> changes, CancellationToken cancellationToken)
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
            //var newDocument = service.FormatAsync(document, textSpans, cancellationToken).WaitAndGetResult(cancellationToken);
            //newDocument.Project.Solution.Workspace.ApplyDocumentChanges(newDocument, cancellationToken);
        }

        /// <summary>
        /// Add the necessary edits to the given spans. Also collect tracking spans over each span.
        ///
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

        private void ToggleBlockComment(Document document, ICommentSelectionService service, SnapshotSpan selectedSpan,
            List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans, CancellationToken cancellationToken)
        {
            var commentInfo = service.GetInfoAsync(document, selectedSpan.Span.ToTextSpan(), cancellationToken).WaitAndGetResult(cancellationToken);

            var blockCommentSelectionContext = new BlockCommentSelectionContext(commentInfo, selectedSpan);

            if (commentInfo.SupportsBlockComment)
            {
                if (TryUncommentBlockComment(blockCommentSelectionContext, textChanges, trackingSpans))
                {
                    return;
                }
                else
                {
                    BlockCommentSpan(blockCommentSelectionContext, textChanges, trackingSpans);
                }
            }
        }

        private bool TryUncommentBlockComment(BlockCommentSelectionContext blockCommentInfo, List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans)
        {
            // If there are not any block comments intersecting the selection, there is nothing to uncomment.
            if (!blockCommentInfo.HasIntersectingBlockComments())
            {
                return false;
            }

            // If the selection begins and ends with a block comment and is entirely commented (or whitespace), try to remove all the block comments inside the selection.
            if (blockCommentInfo.SelectionBeginsAndEndsWithBlockComment() && !blockCommentInfo.SelectionContainsUncommentedNonWhitespaceCharacters())
            {
                if (blockCommentInfo.TryGetBlockCommentsInsideSelection(out var spansInsideSelection))
                {
                    foreach (var spanToRemove in spansInsideSelection)
                    {
                        DeleteBlockComment(textChanges, spanToRemove, blockCommentInfo.CommentSelectionInfo);
                    }
                    trackingSpans.Add(blockCommentInfo.GetTrackingSpan(blockCommentInfo.SelectedSpan, SpanTrackingMode.EdgeExclusive), Operation.Uncomment);
                    return true;
                }

                return false;
            }
            else
            {
                // If the span intersects with any other block comments, pass to the add block comment handler.
                if (blockCommentInfo.HasIntersectingBlockComments())
                {
                    return false;
                }

                return TryUncommentSelectedSpanWithinBlockComment(blockCommentInfo, textChanges, trackingSpans);
            }
        }

        /// <summary>
        /// Check if the selection is entirely contained within a block comment and remove it.
        /// </summary>
        private bool TryUncommentSelectedSpanWithinBlockComment(BlockCommentSelectionContext blockCommentInfo, List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans)
        {
            if (blockCommentInfo.TryGetBlockCommentSurroundingSelection(out var containingSpan))
            {
                trackingSpans.Add(blockCommentInfo.GetTrackingSpan(containingSpan, SpanTrackingMode.EdgeExclusive), Operation.Uncomment);
                DeleteBlockComment(textChanges, containingSpan, blockCommentInfo.CommentSelectionInfo);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Adds a block comment when the selection already contains block comment(s).
        /// The result will be sequential block comments with the entire selection being commented out.
        /// </summary>
        private void AddBlockCommentWithIntersectingSpans(BlockCommentSelectionContext blockCommentInfo, List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans)
        {
            var info = blockCommentInfo.CommentSelectionInfo;
            var selectedSpan = blockCommentInfo.SelectedSpan;
            // If the start of the selected span is uncommented add an open comment.
            if (!blockCommentInfo.IsLocationCommented(selectedSpan.Start))
            {
                InsertText(textChanges, selectedSpan.Start, info.BlockCommentStartString);
            }

            // If the end of the selected span is uncommented add an ending comment marker.
            if (!blockCommentInfo.IsLocationCommented(blockCommentInfo.SelectedSpan.End))
            {
                InsertText(textChanges, selectedSpan.End, info.BlockCommentEndString);
            }

            foreach (var blockCommentSpan in blockCommentInfo.IntersectingBlockCommentSpans)
            {
                // If the block comment begins inside the section, add a close marker before the open marker.
                if (blockCommentSpan.Start > selectedSpan.Start)
                {
                    InsertText(textChanges, blockCommentSpan.Start, info.BlockCommentEndString);
                }

                // If the block comment ends before the end of the selection, add an open marker after the end marker.
                if (blockCommentSpan.End < selectedSpan.End)
                {
                    InsertText(textChanges, blockCommentSpan.End, info.BlockCommentStartString);
                }
            }
            trackingSpans.Add(blockCommentInfo.GetTrackingSpan(blockCommentInfo.SelectedSpan, SpanTrackingMode.EdgeInclusive), Operation.Comment);
        }

        private void BlockCommentSpan(BlockCommentSelectionContext blockCommentInfo, List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans)
        {
            if (blockCommentInfo.HasIntersectingBlockComments())
            {
                AddBlockCommentWithIntersectingSpans(blockCommentInfo, textChanges, trackingSpans);
            }
            else
            {
                AddBlockComment(blockCommentInfo.SelectedSpan, textChanges, trackingSpans, blockCommentInfo.CommentSelectionInfo);
            }
        }

        private void AddBlockComment(SnapshotSpan span, List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans, CommentSelectionInfo commentInfo)
        {
            trackingSpans.Add(span.Snapshot.CreateTrackingSpan(span, SpanTrackingMode.EdgeInclusive), Operation.Comment);
            InsertText(textChanges, span.Start, commentInfo.BlockCommentStartString);
            InsertText(textChanges, span.End, commentInfo.BlockCommentEndString);
        }

        private void DeleteBlockComment(List<TextChange> textChanges, Span spanToRemove, CommentSelectionInfo commentInfo)
        {
            DeleteText(textChanges, new TextSpan(spanToRemove.Start, commentInfo.BlockCommentStartString.Length));
            DeleteText(textChanges, new TextSpan(spanToRemove.End - commentInfo.BlockCommentEndString.Length, commentInfo.BlockCommentEndString.Length));
        }

        /// <summary>
        /// Record "Insert text" text changes.
        /// </summary>
        private void InsertText(List<TextChange> textChanges, int position, string text)
        {
            textChanges.Add(new TextChange(new TextSpan(position, 0), text));
        }

        /// <summary>
        /// Record "Delete text" text changes.
        /// </summary>
        private void DeleteText(List<TextChange> textChanges, TextSpan span)
        {
            textChanges.Add(new TextChange(span, string.Empty));
        }

        internal class BlockCommentSelectionContext
        {
            public CommentSelectionInfo CommentSelectionInfo { get; }

            public SnapshotSpan SelectedSpan { get; }

            public IEnumerable<Span> IntersectingBlockCommentSpans { get; }

            public BlockCommentSelectionContext(CommentSelectionInfo commentSelectionInfo, SnapshotSpan selectedSpan)
            {
                CommentSelectionInfo = commentSelectionInfo;
                SelectedSpan = selectedSpan;
                IntersectingBlockCommentSpans = GetIntersectingBlockCommentedSpans(commentSelectionInfo, selectedSpan);
            }

            /// <summary>
            /// Gets a list of all commented spans.
            /// A commented span is defined by the first open marker until the first close marker.
            /// Once an open marker is found, subsequent open markers are ignored until a closing marker is found.
            /// </summary>
            /// <param name="info">the comment selection info.</param>
            /// <param name="selectedSpan">the selected span.</param>
            /// <returns>a list of all block commented spans after the index, inclusive of the block comment end string.</returns>
            private List<Span> GetIntersectingBlockCommentedSpans(CommentSelectionInfo info, SnapshotSpan selectedSpan)
            {
                var allText = selectedSpan.Snapshot.AsText();
                var commentedSpans = new List<Span>();

                var openIdx = 0;
                while ((openIdx = allText.IndexOf(info.BlockCommentStartString, openIdx, caseSensitive: true)) > 0)
                {
                    // Retrieve the first closing marker located after the open index.
                    var closeIdx = allText.IndexOf(info.BlockCommentEndString, openIdx + info.BlockCommentStartString.Length, caseSensitive: true);

                    // If an open or close marker is not found (-1) or the open marker begins after the selection, no point in continuing.
                    if (openIdx < 0 || closeIdx < 0 || openIdx > selectedSpan.End)
                    {
                        break;
                    }

                    // If there is an intersection with the newly found span and the selected span, add it.
                    var blockCommentSpan = new Span(openIdx, closeIdx + info.BlockCommentEndString.Length - openIdx);
                    if (selectedSpan.IntersectsWith(blockCommentSpan))
                    {
                        commentedSpans.Add(blockCommentSpan);
                    }

                    openIdx = closeIdx;
                }

                return commentedSpans;
            }

            /// <summary>
            /// Checks if the given location is whitespace.
            /// </summary>
            /// <param name="location"></param>
            /// <returns></returns>
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
                return IntersectingBlockCommentSpans.Contains(span => span.Contains(location));
            }

            /// <summary>
            /// Checks if the selection begins and ends with a block comment.
            /// </summary>
            /// <returns></returns>
            public bool SelectionBeginsAndEndsWithBlockComment()
            {
                var spanText = SelectedSpan.GetText();
                var trimmedSpanText = spanText.Trim();
                return trimmedSpanText.StartsWith(CommentSelectionInfo.BlockCommentStartString, StringComparison.Ordinal) && trimmedSpanText.EndsWith(CommentSelectionInfo.BlockCommentEndString, StringComparison.Ordinal);
            }

            /// <summary>
            /// Checks if the selected span contains any uncommented non whitespace characters.
            /// </summary>
            public bool SelectionContainsUncommentedNonWhitespaceCharacters()
            {
                for (int i = SelectedSpan.Start; i <= SelectedSpan.End; i++)
                {
                    if (!IsLocationCommented(i) && !IsLocationWhitespace(i))
                    {
                        return false;
                    }
                }
                return true;
            }

            public bool HasIntersectingBlockComments()
            {
                return !IntersectingBlockCommentSpans.IsEmpty();
            }

            public ITrackingSpan GetTrackingSpan(Span span, SpanTrackingMode spanTrackingMode)
            {
                return SelectedSpan.Snapshot.CreateTrackingSpan(Span.FromBounds(span.Start, span.End), spanTrackingMode);
            }

            public bool TryGetBlockCommentSurroundingSelection(out Span containingSpan)
            {
                containingSpan = IntersectingBlockCommentSpans.FirstOrDefault(commentedSpan => commentedSpan.Contains(SelectedSpan));
                if (containingSpan.Start < 0 || containingSpan.End < 0)
                {
                    return false;
                }

                return true;
            }

            public bool TryGetBlockCommentsInsideSelection(out IEnumerable<Span> blockCommentsInsideSelection)
            {
                blockCommentsInsideSelection = IntersectingBlockCommentSpans.Where(commentSpan => SelectedSpan.Contains(commentSpan));
                return !blockCommentsInsideSelection.IsEmpty();
            }
        }
    }
}
