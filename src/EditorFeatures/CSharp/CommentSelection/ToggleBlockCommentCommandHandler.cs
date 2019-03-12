// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CommentSelection;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Editor.Implementation.CommentSelection;
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
using static Microsoft.CodeAnalysis.Editor.Implementation.CommentSelection.CommentUncommentSelectionCommandHandler;
using VSCommanding = Microsoft.VisualStudio.Commanding;

namespace Microsoft.CodeAnalysis.Editor.CSharp.CommentSelection
{
    [Export(typeof(VSCommanding.ICommandHandler))]
    [ContentType(ContentTypeNames.RoslynContentType)]
    [Name(PredefinedCommandHandlerNames.CommentSelection)]
    internal class ToggleBlockCommentCommandHandler :
        VSCommanding.ICommandHandler<CommentSelectionCommandArgs>
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

        public string DisplayName => EditorFeaturesResources.Toggle_Block_Comment;

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

        internal bool ExecuteCommand(ITextView textView, ITextBuffer subjectBuffer, CommandExecutionContext context)
        {
            var title = EditorFeaturesResources.Toggle_Block_Comment;
            var message = EditorFeaturesResources.Toggling_block_comment_on_selection;

            using (context.OperationContext.AddScope(allowCancellation: false, message))
            {
                var document = subjectBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges();
                if (document == null)
                {
                    return true;
                }

                var getRoot = document.GetSyntaxRootAsync();
                var service = document.GetLanguageService<ICommentSelectionService>();
                if (service == null)
                {
                    return true;
                }

                var trackingSpans = new Dictionary<ITrackingSpan, Operation>();
                var textChanges = new List<TextChange>();

                var root = getRoot.WaitAndGetResult(CancellationToken.None);

                CollectEdits(document, service, root, textView.Selection.GetSnapshotSpansOnBuffer(subjectBuffer), textChanges, trackingSpans, CancellationToken.None);

                var distinctTextChanges = textChanges.Distinct();
                using (var transaction = new CaretPreservingEditTransaction(title, textView, _undoHistoryRegistry, _editorOperationsFactoryService))
                {
                    document.Project.Solution.Workspace.ApplyTextChanges(document.Id, distinctTextChanges, CancellationToken.None);
                    transaction.Complete();
                }

                using (var transaction = new CaretPreservingEditTransaction(title, textView, _undoHistoryRegistry, _editorOperationsFactoryService))
                {
                    Format(service, subjectBuffer.CurrentSnapshot, trackingSpans, CancellationToken.None);
                    transaction.Complete();
                }

                if (trackingSpans.Any())
                {
                    var spans = trackingSpans.Keys.Select(trackingSpan => new Selection(trackingSpan.GetSpan(subjectBuffer.CurrentSnapshot)));
                    textView.GetMultiSelectionBroker().SetSelectionRange(spans, spans.Last());
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

            // Only format uncomment actions.
            var textSpans = changes
                .Where(change => change.Value == Operation.Uncomment)
                .Select(uncommentChange => uncommentChange.Key.GetSpan(snapshot).Span.ToTextSpan())
                .ToImmutableArray();
            var newDocument = service.FormatAsync(document, textSpans, cancellationToken).WaitAndGetResult(cancellationToken);
            newDocument.Project.Solution.Workspace.ApplyDocumentChanges(newDocument, cancellationToken);
        }

        private void CollectEdits(Document document, ICommentSelectionService service, SyntaxNode root, NormalizedSnapshotSpanCollection selectedSpans,
            List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans, CancellationToken cancellationToken)
        {
            if (selectedSpans.IsEmpty())
            {
                return;
            }

            var commentInfo = service.GetInfoAsync(document, selectedSpans.First().Span.ToTextSpan(), cancellationToken).WaitAndGetResult(cancellationToken);
            if (commentInfo.SupportsBlockComment)
            {
                ToggleBlockComment(commentInfo, root, selectedSpans, textChanges, trackingSpans);
            }
        }

        private void ToggleBlockComment(CommentSelectionInfo commentInfo, SyntaxNode root, NormalizedSnapshotSpanCollection selectedSpans,
            List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans)
        {
            var blockCommentedSpans = GetDescendentBlockCommentSpansFromRoot(root);
            var blockCommentSelectionHelpers = selectedSpans.Select(span => new BlockCommentSelectionHelper(blockCommentedSpans, commentInfo, span));

            // If there is a multi selection, either uncomment all or comment all.
            var onlyAddComment = false;
            if (selectedSpans.Count > 1)
            {
                onlyAddComment = blockCommentSelectionHelpers.Where(helper => !helper.IsEntirelyCommented()).Any();
            }

            foreach (var blockCommentSelection in blockCommentSelectionHelpers)
            {
                if (commentInfo.SupportsBlockComment)
                {
                    if (!onlyAddComment && TryUncommentBlockComment(blockCommentedSpans, blockCommentSelection, textChanges, trackingSpans))
                    {
                        //return;
                    }
                    else
                    {
                        BlockCommentSpan(blockCommentSelection, textChanges, trackingSpans, root);
                    }
                }
            }
        }

        private static bool TryUncommentBlockComment(IEnumerable<Span> blockCommentedSpans, BlockCommentSelectionHelper blockCommentSelectionHelper,
            List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans)
        {
            // If the selection is just a caret, try and uncomment blocks on the same line with only whitespace on the line.
            if (blockCommentSelectionHelper.SelectedSpan.IsEmpty && blockCommentSelectionHelper.TryGetBlockCommentOnSameLine(blockCommentedSpans, out var blockCommentOnSameLine))
            {
                DeleteBlockComment(blockCommentSelectionHelper, blockCommentOnSameLine, textChanges);
                trackingSpans.Add(blockCommentSelectionHelper.GetTrackingSpan(blockCommentOnSameLine, SpanTrackingMode.EdgeExclusive), Operation.Uncomment);
                return true;
            }

            // If there are not any block comments intersecting the selection, there is nothing to uncomment.
            if (!blockCommentSelectionHelper.HasIntersectingBlockComments())
            {
                return false;
            }

            // If the selection is entirely commented, remove any block comments that intersect.
            if (blockCommentSelectionHelper.IsEntirelyCommented())
            {
                var intersectingBlockComments = blockCommentSelectionHelper.IntersectingBlockComments;
                foreach (var spanToRemove in intersectingBlockComments)
                {
                    DeleteBlockComment(blockCommentSelectionHelper, spanToRemove, textChanges);
                }
                var trackingSpan = Span.FromBounds(intersectingBlockComments.First().Start, intersectingBlockComments.Last().End);
                trackingSpans.Add(blockCommentSelectionHelper.GetTrackingSpan(trackingSpan, SpanTrackingMode.EdgeExclusive), Operation.Uncomment);
                return true;
            }

            // If the selection is entirely inside a block comment, remove the comment.
            if (blockCommentSelectionHelper.TryGetSurroundingBlockComment(out var containingBlockComment))
            {
                DeleteBlockComment(blockCommentSelectionHelper, containingBlockComment, textChanges);
                trackingSpans.Add(blockCommentSelectionHelper.GetTrackingSpan(containingBlockComment, SpanTrackingMode.EdgeExclusive), Operation.Uncomment);
                return true;
            }

            return false;
        }

        private static void BlockCommentSpan(BlockCommentSelectionHelper blockCommentSelectionHelper, List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans, SyntaxNode root)
        {
            if (blockCommentSelectionHelper.HasIntersectingBlockComments())
            {
                AddBlockCommentWithIntersectingSpans(blockCommentSelectionHelper, textChanges, trackingSpans);
            }
            else
            {
                Span spanToAdd = blockCommentSelectionHelper.SelectedSpan;
                if (blockCommentSelectionHelper.SelectedSpan.IsEmpty)
                {
                    // The location for the comment should be the caret or the location after the end of the token the caret is inside of.
                    var caretLocation = GetLocationAfterToken(blockCommentSelectionHelper.SelectedSpan.Start, root);
                    spanToAdd = Span.FromBounds(caretLocation, caretLocation);
                }

                AddBlockComment(blockCommentSelectionHelper.CommentSelectionInfo, spanToAdd, textChanges);
                trackingSpans.Add(blockCommentSelectionHelper.GetTrackingSpan(spanToAdd, SpanTrackingMode.EdgeInclusive), Operation.Comment);
            }
        }

        /// <summary>
        /// Adds a block comment when the selection already contains block comment(s).
        /// The result will be sequential block comments with the entire selection being commented out.
        /// </summary>
        private static void AddBlockCommentWithIntersectingSpans(BlockCommentSelectionHelper blockCommentSelectionHelper, List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans)
        {
            var info = blockCommentSelectionHelper.CommentSelectionInfo;
            var selectedSpan = blockCommentSelectionHelper.SelectedSpan;
            var spanTrackingMode = SpanTrackingMode.EdgeInclusive;

            // Add comments to all uncommented spans in the selection.
            foreach (var uncommentedSpan in blockCommentSelectionHelper.UncommentedSpansInSelection)
            {
                AddBlockComment(info, uncommentedSpan, textChanges);
            }

            // If the start is commented (and not a comment marker), close the current comment and open a new one.
            if (blockCommentSelectionHelper.IsLocationCommented(selectedSpan.Start) && !blockCommentSelectionHelper.DoesBeginWithBlockComment())
            {
                InsertText(textChanges, selectedSpan.Start, info.BlockCommentEndString);
                InsertText(textChanges, selectedSpan.Start, info.BlockCommentStartString);
            }

            // If the end is commented (and not a comment marker), close the current comment and open a new one.
            if (blockCommentSelectionHelper.IsLocationCommented(selectedSpan.End) && !blockCommentSelectionHelper.DoesEndWithBlockComment())
            {
                InsertText(textChanges, selectedSpan.End, info.BlockCommentEndString);
                InsertText(textChanges, selectedSpan.End, info.BlockCommentStartString);
            }

            trackingSpans.Add(blockCommentSelectionHelper.GetTrackingSpan(selectedSpan, spanTrackingMode), Operation.Comment);
        }

        private static void AddBlockComment(CommentSelectionInfo commentInfo, Span span, List<TextChange> textChanges)
        {
            InsertText(textChanges, span.Start, commentInfo.BlockCommentStartString);
            InsertText(textChanges, span.End, commentInfo.BlockCommentEndString);
        }

        private static void DeleteBlockComment(BlockCommentSelectionHelper blockCommentSelectionHelper, Span spanToRemove, List<TextChange> textChanges)
        {
            var commentInfo = blockCommentSelectionHelper.CommentSelectionInfo;
            DeleteText(textChanges, new TextSpan(spanToRemove.Start, commentInfo.BlockCommentStartString.Length));

            var blockCommentMarkerPosition = spanToRemove.End - commentInfo.BlockCommentEndString.Length;
            // Sometimes the block comment will be missing a close marker.
            if (Equals(blockCommentSelectionHelper.GetSubstringFromText(blockCommentMarkerPosition, commentInfo.BlockCommentEndString.Length), commentInfo.BlockCommentEndString))
            {
                DeleteText(textChanges, new TextSpan(blockCommentMarkerPosition, commentInfo.BlockCommentEndString.Length));
            }
        }

        private static void InsertText(List<TextChange> textChanges, int position, string text)
        {
            textChanges.Add(new TextChange(new TextSpan(position, 0), text));
        }

        private static void DeleteText(List<TextChange> textChanges, TextSpan span)
        {
            textChanges.Add(new TextChange(span, string.Empty));
        }

        private static IEnumerable<Span> GetDescendentBlockCommentSpansFromRoot(SyntaxNode root)
        {
            return root.DescendantTrivia()
                .Where(trivia => trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                .Select(blockCommentTrivia => blockCommentTrivia.Span.ToSpan());
        }

        /// <summary>
        /// Get a location of itself or the end of the token it is located in.
        /// </summary>
        private static int GetLocationAfterToken(int location, SyntaxNode root)
        {
            var token = root.FindToken(location);
            if (token.Span.Contains(location))
            {
                return token.Span.End;
            }
            return location;
        }

        private class BlockCommentSelectionHelper
        {
            private readonly string _text;

            public CommentSelectionInfo CommentSelectionInfo { get; }

            public SnapshotSpan SelectedSpan { get; }

            public IEnumerable<Span> IntersectingBlockComments { get; }

            private readonly Lazy<IEnumerable<Span>> _uncommentedSpansInSelection;
            public IEnumerable<Span> UncommentedSpansInSelection
            {
                get { return _uncommentedSpansInSelection.Value; }
            }

            public BlockCommentSelectionHelper(IEnumerable<Span> allBlockComments, CommentSelectionInfo commentSelectionInfo, SnapshotSpan selectedSpan)
            {
                _text = selectedSpan.GetText().Trim();

                CommentSelectionInfo = commentSelectionInfo;
                SelectedSpan = selectedSpan;
                IntersectingBlockComments = GetIntersectingBlockComments(allBlockComments, selectedSpan);

                // Lazily evaluate this, it's not used in every case.
                _uncommentedSpansInSelection = new Lazy<IEnumerable<Span>>(GetUncommentedSpansInSelection);
            }

            /// <summary>
            /// Determines if the given span is entirely whitespace.
            /// </summary>
            /// <param name="span">the span to check for whitespace.</param>
            /// <returns>true if the span is entirely whitespace.</returns>
            public bool IsSpanWhitespace(Span span)
            {
                for (var i = span.Start; i < span.End; i++)
                {
                    if (!IsLocationWhitespace(i))
                    {
                        return false;
                    }
                }

                return true;

                bool IsLocationWhitespace(int location)
                {
                    var character = SelectedSpan.Snapshot.GetPoint(location).GetChar();
                    return SyntaxFacts.IsWhitespace(character) || SyntaxFacts.IsNewLine(character);
                }
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
                return _text.StartsWith(CommentSelectionInfo.BlockCommentStartString, StringComparison.Ordinal)
                       || _text.StartsWith(CommentSelectionInfo.BlockCommentEndString, StringComparison.Ordinal);
            }

            public bool DoesEndWithBlockComment()
            {
                return _text.EndsWith(CommentSelectionInfo.BlockCommentStartString, StringComparison.Ordinal)
                       || _text.EndsWith(CommentSelectionInfo.BlockCommentEndString, StringComparison.Ordinal);
            }

            /// <summary>
            /// Checks if the selected span contains any uncommented non whitespace characters.
            /// </summary>
            public bool IsEntirelyCommented()
            {
                return !UncommentedSpansInSelection.Any();
            }

            /// <summary>
            /// Returns if the selection intersects with any block comments.
            /// </summary>
            public bool HasIntersectingBlockComments()
            {
                return IntersectingBlockComments.Any();
            }

            public string GetSubstringFromText(int position, int length)
            {
                return SelectedSpan.Snapshot.GetText().Substring(position, length);
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
                if (containingSpan.Start == 0 && containingSpan.End == 0)
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
            public bool TryGetBlockCommentOnSameLine(IEnumerable<Span> allBlockComments, out Span commentedSpanOnSameLine)
            {
                var selectedLine = SelectedSpan.Snapshot.GetLineFromPosition(SelectedSpan.Start);

                var lineStartToCaretIsWhitespace = IsSpanWhitespace(Span.FromBounds(selectedLine.Start, SelectedSpan.Start));
                var caretToLineEndIsWhitespace = IsSpanWhitespace(Span.FromBounds(SelectedSpan.Start, selectedLine.End));
                foreach (var blockComment in allBlockComments)
                {
                    if (lineStartToCaretIsWhitespace && SelectedSpan.Start < blockComment.Start && SelectedSpan.Snapshot.AreOnSameLine(SelectedSpan.Start, blockComment.Start))
                    {
                        if (IsSpanWhitespace(Span.FromBounds(SelectedSpan.Start, blockComment.Start)))
                        {
                            commentedSpanOnSameLine = blockComment;
                            return true;
                        }
                    }
                    else if (caretToLineEndIsWhitespace && SelectedSpan.Start > blockComment.End && SelectedSpan.Snapshot.AreOnSameLine(SelectedSpan.Start, blockComment.End))
                    {
                        if (IsSpanWhitespace(Span.FromBounds(blockComment.End, SelectedSpan.Start)))
                        {
                            commentedSpanOnSameLine = blockComment;
                            return true;
                        }
                    }
                }

                commentedSpanOnSameLine = new Span();
                return false;
            }

            /// <summary>
            /// Gets a list of block comments that intersect the span.
            /// Spans are intersecting if 1 location is the same between them (empty spans look at the start).
            /// </summary>
            private IEnumerable<Span> GetIntersectingBlockComments(IEnumerable<Span> allBlockComments, Span span)
            {
                return allBlockComments.Where(blockCommentSpan => span.OverlapsWith(blockCommentSpan) || blockCommentSpan.Contains(span));
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
                    if (commentedSpan.Start > spanStart)
                    {
                        // Get span up until the comment and check to make sure it is not whitespace.
                        var possibleUncommentedSpan = Span.FromBounds(spanStart, commentedSpan.Start);
                        if (!IsSpanWhitespace(possibleUncommentedSpan))
                        {
                            uncommentedSpans.Add(possibleUncommentedSpan);
                        }
                    }

                    // The next possible uncommented span starts at the end of this commented span.
                    spanStart = commentedSpan.End;
                }

                // If part of the selection is left over, it's not commented.  Add if not whitespace.
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
        }
    }
}
