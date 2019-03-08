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
using VSCommanding = Microsoft.VisualStudio.Commanding;

namespace Microsoft.CodeAnalysis.Editor.CSharp.CommentSelection
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

                CollectEdits(
                    document, service, root, textView.Selection.GetSnapshotSpansOnBuffer(subjectBuffer),
                    textChanges, trackingSpans, CancellationToken.None);

                using (var transaction = new CaretPreservingEditTransaction(title, textView, _undoHistoryRegistry, _editorOperationsFactoryService))
                {
                    document.Project.Solution.Workspace.ApplyTextChanges(document.Id, textChanges, CancellationToken.None);
                    transaction.Complete();
                }

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

            // Only format uncomment actions.
            var textSpans = changes
                .Where(change => change.Value == Operation.Uncomment)
                .Select(uncommentChange => uncommentChange.Key.GetSpan(snapshot).Span.ToTextSpan())
                .ToImmutableArray();
            var newDocument = service.FormatAsync(document, textSpans, cancellationToken).WaitAndGetResult(cancellationToken);
            newDocument.Project.Solution.Workspace.ApplyDocumentChanges(newDocument, cancellationToken);
        }

        private void CollectEdits(
            Document document, ICommentSelectionService service, SyntaxNode root, NormalizedSnapshotSpanCollection selectedSpans,
            List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans, CancellationToken cancellationToken)
        {
            var blockCommentedSpans = GetDescendentBlockCommentSpansFromRoot(root);

            foreach (var span in selectedSpans)
            {
                var commentInfo = service.GetInfoAsync(document, span.Span.ToTextSpan(), cancellationToken).WaitAndGetResult(cancellationToken);
                var blockCommentSelection = new BlockCommentSelectionHelper(blockCommentedSpans, commentInfo, span);
                if (commentInfo.SupportsBlockComment)
                {
                    if (TryUncommentBlockComment(blockCommentedSpans, blockCommentSelection, textChanges, trackingSpans))
                    {
                        return;
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
                DeleteBlockComment(blockCommentSelectionHelper, textChanges, blockCommentOnSameLine);
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
                    DeleteBlockComment(blockCommentSelectionHelper, textChanges, spanToRemove);
                }
                var trackingSpan = Span.FromBounds(intersectingBlockComments.First().Start, intersectingBlockComments.Last().End);
                trackingSpans.Add(blockCommentSelectionHelper.GetTrackingSpan(trackingSpan, SpanTrackingMode.EdgeExclusive), Operation.Uncomment);
                return true;
            }

            // If the selection is entirely inside a block comment, remove the comment.
            if (blockCommentSelectionHelper.TryGetSurroundingBlockComment(out var containingSpan))
            {
                DeleteBlockComment(blockCommentSelectionHelper, textChanges, containingSpan);
                trackingSpans.Add(blockCommentSelectionHelper.GetTrackingSpan(containingSpan, SpanTrackingMode.EdgeExclusive), Operation.Uncomment);
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
                    // Get span at the caret location or after the token that the location is inside.
                    var caretLocation = GetLocationAfterToken(blockCommentSelectionHelper.SelectedSpan.Start, root);
                    spanToAdd = Span.FromBounds(caretLocation, caretLocation);
                }

                AddBlockComment(blockCommentSelectionHelper, spanToAdd, textChanges, trackingSpans);
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
                InsertText(textChanges, uncommentedSpan.Start, info.BlockCommentStartString);
                InsertText(textChanges, uncommentedSpan.End, info.BlockCommentEndString);
                trackingSpans.Add(blockCommentSelectionHelper.GetTrackingSpan(selectedSpan, spanTrackingMode), Operation.Comment);
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
        }

        private static void AddBlockComment(BlockCommentSelectionHelper blockCommentSelectionHelper, Span span, List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans)
        {
            trackingSpans.Add(blockCommentSelectionHelper.GetTrackingSpan(span, SpanTrackingMode.EdgeInclusive), Operation.Comment);
            InsertText(textChanges, span.Start, blockCommentSelectionHelper.CommentSelectionInfo.BlockCommentStartString);
            InsertText(textChanges, span.End, blockCommentSelectionHelper.CommentSelectionInfo.BlockCommentEndString);
        }

        private static void DeleteBlockComment(BlockCommentSelectionHelper blockCommentSelectionHelper, List<TextChange> textChanges, Span spanToRemove)
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
                return UncommentedSpansInSelection.IsEmpty();
            }

            /// <summary>
            /// Returns if the selection intersects with any block comments.
            /// </summary>
            public bool HasIntersectingBlockComments()
            {
                return !IntersectingBlockComments.IsEmpty();
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
                var spanFromLineStartToCaret = Span.FromBounds(selectedLine.Start, SelectedSpan.Start);
                var spanFromCaretToLineEnd = Span.FromBounds(SelectedSpan.Start, selectedLine.End);

                if (IsSpanWhitespace(spanFromLineStartToCaret))
                {
                    // There is whitespace from the line start to the caret.
                    // Check for block comment start markers after the caret.
                    if (TryGetBlockCommentWithOnlyWhitespaceBetween(allBlockComments, useStartMarkerPosition: true, out var span))
                    {
                        commentedSpanOnSameLine = span;
                        return true;
                    }
                }
                else if (IsSpanWhitespace(spanFromCaretToLineEnd))
                {
                    // Whitespace from caret to end of line.
                    // Check for block comment end markers before the caret.
                    if (TryGetBlockCommentWithOnlyWhitespaceBetween(allBlockComments, useStartMarkerPosition: false, out var span))
                    {
                        commentedSpanOnSameLine = span;
                        return true;
                    }
                }

                commentedSpanOnSameLine = new Span();
                return false;

                bool TryGetBlockCommentWithOnlyWhitespaceBetween(IEnumerable<Span> allBlockComments, bool useStartMarkerPosition, out Span span)
                {
                    var blockCommentSpansOnSameLine = allBlockComments
                        .Where(blockCommentSpan => SelectedSpan.Snapshot.AreOnSameLine(SelectedSpan.Start, GetCommentPositionToCheck(blockCommentSpan, useStartMarkerPosition)));
                    if (!blockCommentSpansOnSameLine.IsEmpty())
                    {
                        foreach (var blockCommentSpan in blockCommentSpansOnSameLine)
                        {
                            // If we're using the start marker, check from caret -> start marker.  Otherwise we're using the end marker and check end -> caret.
                            var spanToCheckForWhitespace = useStartMarkerPosition ? Span.FromBounds(SelectedSpan.Start, blockCommentSpan.Start) : Span.FromBounds(blockCommentSpan.End, SelectedSpan.Start);
                            if (IsSpanWhitespace(spanToCheckForWhitespace))
                            {
                                span = blockCommentSpan;
                                return true;
                            }
                        }
                    }

                    span = new Span();
                    return false;

                    int GetCommentPositionToCheck(Span blockCommentSpan, bool useStartMarkerPosition) => useStartMarkerPosition ? blockCommentSpan.Start : blockCommentSpan.End;
                }
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
                    // Only add the span if the commented span starts after the current position.
                    if (commentedSpan.Start > spanStart)
                    {
                        var newSpanEnd = commentedSpan.Start;
                        var uncommentedSpan = Span.FromBounds(spanStart, newSpanEnd);
                        // Only add the span if it is not whitespace.
                        if (!IsSpanWhitespace(uncommentedSpan))
                        {
                            uncommentedSpans.Add(uncommentedSpan);
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
        }
    }
}
