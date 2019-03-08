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
            var title = EditorFeaturesResources.Comment_Selection;

            var message = EditorFeaturesResources.Commenting_currently_selected_text;

            using (context.OperationContext.AddScope(allowCancellation: false, message))
            {
                var document = subjectBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges();
                if (document == null)
                {
                    return true;
                }

                var getRootTask = document.GetSyntaxRootAsync();
                var service = document.GetLanguageService<ICommentSelectionService>();
                if (service == null)
                {
                    return true;
                }

                var trackingSpans = new Dictionary<ITrackingSpan, Operation>();
                var textChanges = new List<TextChange>();
                CollectEdits(
                    document, service, getRootTask, textView.Selection.GetSnapshotSpansOnBuffer(subjectBuffer),
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
            Document document, ICommentSelectionService service, Task<SyntaxNode> getRootTask, NormalizedSnapshotSpanCollection selectedSpans,
            List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans, CancellationToken cancellationToken)
        {
            foreach (var span in selectedSpans)
            {
                ToggleBlockComment(document, service, getRootTask, span, textChanges, trackingSpans, cancellationToken);
            }
        }

        private static void ToggleBlockComment(Document document, ICommentSelectionService service, Task<SyntaxNode> getRootTask, SnapshotSpan selectedSpan,
            List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans, CancellationToken cancellationToken)
        {
            var commentInfo = service.GetInfoAsync(document, selectedSpan.Span.ToTextSpan(), cancellationToken).WaitAndGetResult(cancellationToken);
            var root = getRootTask.WaitAndGetResult(cancellationToken);

            var blockCommentSelection = new BlockCommentSelection(root, commentInfo, selectedSpan);

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
                    DeleteBlockComment(blockCommentSelection, textChanges, spanToRemove);
                }
                var trackingSpan = Span.FromBounds(intersectingBlockComments.First().Start, intersectingBlockComments.Last().End);
                trackingSpans.Add(blockCommentSelection.GetTrackingSpan(trackingSpan, SpanTrackingMode.EdgeExclusive), Operation.Uncomment);
                return true;
            }
            else
            {
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
                DeleteBlockComment(blockCommentSelection, textChanges, blockCommentOnSameLine);
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
                DeleteBlockComment(blockCommentSelection, textChanges, containingSpan);
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
                if (blockCommentSelection.SelectedSpan.IsEmpty)
                {
                    var caretLocation = blockCommentSelection.GetCaretLocationAfterToken();
                    AddBlockComment(blockCommentSelection, Span.FromBounds(caretLocation, caretLocation), textChanges, trackingSpans);
                }
                else
                {
                    AddBlockComment(blockCommentSelection, blockCommentSelection.SelectedSpan, textChanges, trackingSpans);
                }
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

        private static void AddBlockComment(BlockCommentSelection blockCommentSelection, Span span, List<TextChange> textChanges, IDictionary<ITrackingSpan, Operation> trackingSpans)
        {
            trackingSpans.Add(blockCommentSelection.GetTrackingSpan(span, SpanTrackingMode.EdgeInclusive), Operation.Comment);
            InsertText(textChanges, span.Start, blockCommentSelection.CommentSelectionInfo.BlockCommentStartString);
            InsertText(textChanges, span.End, blockCommentSelection.CommentSelectionInfo.BlockCommentEndString);
        }

        private static void DeleteBlockComment(BlockCommentSelection blockCommentSelection, List<TextChange> textChanges, Span spanToRemove)
        {
            var commentInfo = blockCommentSelection.CommentSelectionInfo;
            DeleteText(textChanges, new TextSpan(spanToRemove.Start, commentInfo.BlockCommentStartString.Length));

            var blockCommentMarkerPosition = spanToRemove.End - commentInfo.BlockCommentEndString.Length;
            // Sometimes the block comment will be missing a close marker.
            if (Equals(blockCommentSelection.GetSubstringFromText(blockCommentMarkerPosition, commentInfo.BlockCommentEndString.Length), commentInfo.BlockCommentEndString))
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

        private class BlockCommentSelection
        {
            private readonly IEnumerable<Span> _descendentBlockCommentSpansFromRoot;
            private readonly string _text;
            private readonly SyntaxNode _root;

            public CommentSelectionInfo CommentSelectionInfo { get; }

            public SnapshotSpan SelectedSpan { get; }

            public IEnumerable<Span> IntersectingBlockComments { get; }

            private readonly Lazy<IEnumerable<Span>> _uncommentedSpansInSelection;
            public IEnumerable<Span> UncommentedSpansInSelection
            {
                get { return _uncommentedSpansInSelection.Value; }
            }

            public BlockCommentSelection(SyntaxNode root, CommentSelectionInfo commentSelectionInfo, SnapshotSpan selectedSpan)
            {
                _root = root;
                _text = selectedSpan.GetText().Trim();
                _descendentBlockCommentSpansFromRoot = GetDescendentBlockCommentSpansFromRoot();

                CommentSelectionInfo = commentSelectionInfo;
                SelectedSpan = selectedSpan;
                IntersectingBlockComments = GetIntersectingBlockComments();

                // Lazily evaluate this, it's not used in every case.
                _uncommentedSpansInSelection = new Lazy<IEnumerable<Span>>(GetUncommentedSpansInSelection);
            }

            /// <summary>
            /// Gets the descendent block comment spans from the root node.
            /// </summary>
            private IEnumerable<Span> GetDescendentBlockCommentSpansFromRoot()
            {
                return _root.DescendantTrivia()
                    .Where(trivia => trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                    .Select(blockCommentTrivia => blockCommentTrivia.Span.ToSpan());
            }

            /// <summary>
            /// Gets a list of block comments that intersect the selected span.
            /// Spans are intersecting if 1 location is the same between them (empty spans look at the start).
            /// </summary>
            private IEnumerable<Span> GetIntersectingBlockComments()
            {
                return _descendentBlockCommentSpansFromRoot.Where(blockCommentSpan => SelectedSpan.OverlapsWith(blockCommentSpan) || blockCommentSpan.Contains(SelectedSpan));
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
            /// Gets a valid location for the caret.
            /// If it is inside a token, return the location at the end of the token.
            /// </summary>
            public int GetCaretLocationAfterToken()
            {
                var currentCaretLocation = SelectedSpan.Start;
                var token = _root.FindToken(currentCaretLocation);
                if (token.Span.Contains(currentCaretLocation))
                {
                    return token.Span.End;
                }
                return currentCaretLocation;
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
            public bool TryGetBlockCommentOnSameLine(out Span commentedSpanOnSameLine)
            {
                var allText = SelectedSpan.Snapshot.AsText();
                var selectedLine = SelectedSpan.Snapshot.GetLineFromPosition(SelectedSpan.Start);

                var spanFromLineStartToCaret = Span.FromBounds(selectedLine.Start, SelectedSpan.Start);
                var spanFromCaretToLineEnd = Span.FromBounds(SelectedSpan.Start, selectedLine.End);

                if (IsSpanWhitespace(spanFromLineStartToCaret))
                {
                    // There is whitespace from the line start to the caret.
                    // Check for block comments beginning after the caret.
                    var blockCommentSpansOnSameLine = GetBlockCommentSpansOnSameLine(findSpanAfterCaret: true);
                    if (!blockCommentSpansOnSameLine.IsEmpty())
                    {
                        foreach(var blockCommentSpan in blockCommentSpansOnSameLine)
                        {
                            if (IsSpanWhitespace(Span.FromBounds(SelectedSpan.Start, blockCommentSpan.Start)))
                            {
                                commentedSpanOnSameLine = blockCommentSpan;
                                return true;
                            }
                        }
                    }
                }
                else if (IsSpanWhitespace(spanFromCaretToLineEnd))
                {
                    // Whitespace from caret to end of line.
                    // Check for block comments ending before the caret.
                    var blockCommentSpansOnSameLine = GetBlockCommentSpansOnSameLine(findSpanAfterCaret: false);
                    if (!blockCommentSpansOnSameLine.IsEmpty())
                    {
                        foreach (var blockCommentSpan in blockCommentSpansOnSameLine)
                        {
                            if (IsSpanWhitespace(Span.FromBounds(blockCommentSpan.End, SelectedSpan.Start)))
                            {
                                commentedSpanOnSameLine = blockCommentSpan;
                                return true;
                            }
                        }
                    }
                }

                commentedSpanOnSameLine = new Span();
                return false;

                // Get block comments on the same line (before or after the caret).
                IEnumerable<Span> GetBlockCommentSpansOnSameLine(bool findSpanAfterCaret)
                {
                    return _descendentBlockCommentSpansFromRoot.Where(blockCommentSpan => IsBlockCommentOnSameLine(blockCommentSpan, findSpanAfterCaret));
                }

                // Determines if the block comment span is partially on the same line as the caret.
                // If we're looking for a comment after the caret, only check the location of the start comment marker.
                // If we're looking for a comment before the caret, only check the location of the end comment marker.
                bool IsBlockCommentOnSameLine(Span blockCommentSpan, bool findSpanAfterCaret)
                {
                    if (findSpanAfterCaret)
                    {
                        // The start of the span should begin after the caret on the same line.
                        return selectedLine.LineNumber == SelectedSpan.Snapshot.GetLineFromPosition(blockCommentSpan.Start).LineNumber;
                    }
                    else
                    {
                        // The end of the span should close before the caret on the same line.
                        return selectedLine.LineNumber == SelectedSpan.Snapshot.GetLineFromPosition(blockCommentSpan.End).LineNumber;
                    }
                }
            }
        }
    }
}
