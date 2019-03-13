// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CommentSelection;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Experiments;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.CSharp.CommentSelection
{
    internal struct CommentSelectionResult
    {
        /// <summary>
        /// Text changes to make for this operation.
        /// </summary>
        public IEnumerable<TextChange> TextChanges { get; }
        /// <summary>
        /// Tracking spans used to format and set the output selection after edits.
        /// </summary>
        public IEnumerable<CommentTrackingSpan> TrackingSpans { get; }

        public CommentSelectionResult(IEnumerable<TextChange> textChanges, IEnumerable<CommentTrackingSpan> trackingSpans)
        {
            TextChanges = textChanges;
            TrackingSpans = trackingSpans;
        }
    }

    internal struct CommentTrackingSpan
    {
        private readonly TextSpan _trackingSpan;

        // In some cases, the tracking span needs to be adjusted by a specific amount after the changes have been applied.
        // These fields store the amount to adjust the span by after edits have been applied.
        private readonly int _amountToAddToStart;
        private readonly int _amountToAddToEnd;

        public CommentTrackingSpan(TextSpan trackingSpan)
        {
            _trackingSpan = trackingSpan;
            _amountToAddToStart = 0;
            _amountToAddToEnd = 0;
        }

        public CommentTrackingSpan(TextSpan trackingSpan, int amountToAddToStart, int amountToAddToEnd)
        {
            _trackingSpan = trackingSpan;
            _amountToAddToStart = amountToAddToStart;
            _amountToAddToEnd = amountToAddToEnd;
        }

        /*
        public Selection ToSelection(ITextBuffer buffer)
        {
            return new Selection(ToSnapshotSpan(buffer.CurrentSnapshot));
        }

        
        public SnapshotSpan ToSnapshotSpan(ITextSnapshot snapshot)
        {
            var snapshotSpan = _trackingSpan.GetSpan(snapshot);
            if (_amountToAddToStart != 0 || _amountToAddToEnd != 0)
            {
                var updatedStart = snapshotSpan.Start.Position + _amountToAddToStart;
                var updatedEnd = snapshotSpan.End.Position + _amountToAddToEnd;
                if (updatedStart >= snapshotSpan.Start.Position && updatedEnd <= snapshotSpan.End.Position)
                {
                    snapshotSpan = new SnapshotSpan(snapshot, Span.FromBounds(updatedStart, updatedEnd));
                }
            }

            return snapshotSpan;
        }*/
    }

    [ExportLanguageService(typeof(ICommentSelectionService), LanguageNames.CSharp), Shared]
    internal class CSharpCommentSelectionService : AbstractCommentSelectionService
    {
        public override string SingleLineCommentString => "//";
        public override bool SupportsBlockComment => true;
        public override string BlockCommentStartString => "/*";
        public override string BlockCommentEndString => "*/";

        public async override Task<Document> ToggleBlockComment(Document document, IEnumerable<TextSpan> selectedSpans, CancellationToken cancellationToken)
        {
            var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var results = await CollectEdits(document, sourceText, selectedSpans, cancellationToken).ConfigureAwait(false);
            var newText = sourceText.WithChanges(results.TextChanges);
            var newDocument = document.WithText(newText);
            return newDocument;
        }

        internal async Task<CommentSelectionResult> CollectEdits(Document document, SourceText sourceText, IEnumerable<TextSpan> selectedSpans,
            CancellationToken cancellationToken)
        {
            var emptyResult = new CommentSelectionResult(new List<TextChange>(), new List<CommentTrackingSpan>());
            var experimentationService = document.Project.Solution.Workspace.Services.GetRequiredService<IExperimentationService>();
            if (!experimentationService.IsExperimentEnabled(WellKnownExperimentNames.RoslynToggleBlockComment))
            {
                return emptyResult;
            }

            var root = document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var commentInfo = this;

            if (commentInfo.SupportsBlockComment)
            {
                return ToggleBlockComments(await root, sourceText, selectedSpans);
            }

            return emptyResult;
        }


        private CommentSelectionResult ToggleBlockComments(SyntaxNode root, SourceText sourceText, IEnumerable<TextSpan> selectedSpans)
        {
            var blockCommentedSpans = GetDescendentBlockCommentSpansFromRoot(root);
            var blockCommentSelectionHelpers = selectedSpans.Select(span => new BlockCommentSelectionHelper(blockCommentedSpans, sourceText, span)).ToList();

            var uncommentChanges = new List<TextChange>();
            var uncommentTrackingSpans = new List<CommentTrackingSpan>();
            var shouldAddComments = false;
            // Try to uncomment until an uncommented span is found.
            foreach (var blockCommentSelection in blockCommentSelectionHelpers)
            {
                // If any selection does not have comments to remove, then the operation should be comment.
                if (!TryUncommentBlockComment(blockCommentedSpans, blockCommentSelection, uncommentChanges, uncommentTrackingSpans))
                {
                    shouldAddComments = true;
                    break;
                }
            }

            if (shouldAddComments)
            {
                var commentChanges = new List<TextChange>();
                var commentTrackingSpans = new List<CommentTrackingSpan>();
                blockCommentSelectionHelpers.ForEach(
                    blockCommentSelection => BlockCommentSpan(blockCommentSelection, root, commentChanges, commentTrackingSpans));
                return new CommentSelectionResult(commentChanges, commentTrackingSpans);
            }
            else
            {
                return new CommentSelectionResult(uncommentChanges, uncommentTrackingSpans);
            }
        }

        private bool TryUncommentBlockComment(IEnumerable<SyntaxTrivia> blockCommentedSpans,
            BlockCommentSelectionHelper blockCommentSelectionHelper, List<TextChange> textChanges,
            List<CommentTrackingSpan> trackingSpans)
        {
            // If the selection is just a caret, try and uncomment blocks on the same line with only whitespace on the line.
            if (blockCommentSelectionHelper.SelectedSpan.IsEmpty && blockCommentSelectionHelper.TryGetBlockCommentOnSameLine(blockCommentedSpans, out var blockCommentOnSameLine))
            {
                DeleteBlockComment(blockCommentSelectionHelper, blockCommentOnSameLine, textChanges);
                //trackingSpans.Add(blockCommentSelectionHelper.GetTrackingSpan(blockCommentOnSameLine, SpanTrackingMode.EdgeExclusive));
                return true;
            }
            // If the selection is entirely commented, remove any block comments that intersect.
            else if (blockCommentSelectionHelper.IsEntirelyCommented())
            {
                var intersectingBlockComments = blockCommentSelectionHelper.IntersectingBlockComments;
                foreach (var spanToRemove in intersectingBlockComments)
                {
                    DeleteBlockComment(blockCommentSelectionHelper, spanToRemove, textChanges);
                }
                var trackingSpan = TextSpan.FromBounds(intersectingBlockComments.First().Span.Start, intersectingBlockComments.Last().Span.End);
                //trackingSpans.Add(blockCommentSelectionHelper.GetTrackingSpan(trackingSpan, SpanTrackingMode.EdgeExclusive));
                return true;
            }
            else
            {
                return false;
            }
        }

        private void BlockCommentSpan(BlockCommentSelectionHelper blockCommentSelectionHelper, SyntaxNode root,
            List<TextChange> textChanges, List<CommentTrackingSpan> trackingSpans)
        {
            if (blockCommentSelectionHelper.HasIntersectingBlockComments())
            {
                AddBlockCommentWithIntersectingSpans(blockCommentSelectionHelper, textChanges, trackingSpans);
            }
            else
            {
                TextSpan spanToAdd = blockCommentSelectionHelper.SelectedSpan;
                if (blockCommentSelectionHelper.SelectedSpan.IsEmpty)
                {
                    // The location for the comment should be the caret or the location after the end of the token the caret is inside of.
                    var caretLocation = GetLocationAfterToken(blockCommentSelectionHelper.SelectedSpan.Start, root);
                    spanToAdd = TextSpan.FromBounds(caretLocation, caretLocation);
                }

                //trackingSpans.Add(blockCommentSelectionHelper.GetTrackingSpan(spanToAdd, SpanTrackingMode.EdgeInclusive));
                AddBlockComment(spanToAdd, textChanges);
            }
        }

        /// <summary>
        /// Adds a block comment when the selection already contains block comment(s).
        /// The result will be sequential block comments with the entire selection being commented out.
        /// </summary>
        private void AddBlockCommentWithIntersectingSpans(BlockCommentSelectionHelper blockCommentSelectionHelper,
            List<TextChange> textChanges, List<CommentTrackingSpan> trackingSpans)
        {
            var selectedSpan = blockCommentSelectionHelper.SelectedSpan;
            //var spanTrackingMode = SpanTrackingMode.EdgeInclusive;

            var amountToAddToStart = 0;
            var amountToAddToEnd = 0;

            // Add comments to all uncommented spans in the selection.
            foreach (var uncommentedSpan in blockCommentSelectionHelper.UncommentedSpansInSelection)
            {
                AddBlockComment(uncommentedSpan, textChanges);
            }

            // If the start is commented (and not a comment marker), close the current comment and open a new one.
            if (blockCommentSelectionHelper.IsLocationCommented(selectedSpan.Start)
                && !blockCommentSelectionHelper.DoesBeginWithBlockComment(BlockCommentStartString, BlockCommentEndString))
            {
                InsertText(textChanges, selectedSpan.Start, BlockCommentEndString);
                InsertText(textChanges, selectedSpan.Start, BlockCommentStartString);
                // Shrink the tracking so the previous comment start marker is not included in selection.
                amountToAddToStart = BlockCommentEndString.Length;
            }

            // If the end is commented (and not a comment marker), close the current comment and open a new one.
            if (blockCommentSelectionHelper.IsLocationCommented(selectedSpan.End)
                && !blockCommentSelectionHelper.DoesEndWithBlockComment(BlockCommentStartString, BlockCommentEndString))
            {
                InsertText(textChanges, selectedSpan.End, BlockCommentEndString);
                InsertText(textChanges, selectedSpan.End, BlockCommentStartString);
                // Shrink the tracking span so the next comment start marker is not included in selection.
                amountToAddToEnd = -BlockCommentStartString.Length;
            }

            //var trackingSpan = blockCommentSelectionHelper.GetTrackingSpan(selectedSpan, spanTrackingMode, amountToAddToStart, amountToAddToEnd);
            //trackingSpans.Add(trackingSpan);
        }

        private void AddBlockComment(TextSpan span, List<TextChange> textChanges)
        {
            InsertText(textChanges, span.Start, BlockCommentStartString);
            InsertText(textChanges, span.End, BlockCommentEndString);
        }

        private void DeleteBlockComment(BlockCommentSelectionHelper blockCommentSelectionHelper, SyntaxTrivia commentToRemove,
            List<TextChange> textChanges)
        {
            DeleteText(textChanges, new TextSpan(commentToRemove.Span.Start, BlockCommentStartString.Length));
            var blockCommentMarkerPosition = commentToRemove.Span.End - BlockCommentEndString.Length;
            if (commentToRemove.ContainsDiagnostics)
            {
                var diag = commentToRemove.GetDiagnostics();
            }
            DeleteText(textChanges, new TextSpan(blockCommentMarkerPosition, BlockCommentEndString.Length));
        }

        protected void InsertText(List<TextChange> textChanges, int position, string text)
        {
            textChanges.Add(new TextChange(new TextSpan(position, 0), text));
        }

        protected void DeleteText(List<TextChange> textChanges, TextSpan span)
        {
            textChanges.Add(new TextChange(span, string.Empty));
        }

        private IEnumerable<SyntaxTrivia> GetDescendentBlockCommentSpansFromRoot(SyntaxNode root)
        {
            return root.DescendantTrivia()
                .Where(trivia => trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
        }

        /// <summary>
        /// Get a location of itself or the end of the token it is located in.
        /// </summary>
        private int GetLocationAfterToken(int location, SyntaxNode root)
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
            private readonly SourceText _sourceText;

            public TextSpan SelectedSpan { get; }

            public IEnumerable<SyntaxTrivia> IntersectingBlockComments { get; }

            public IEnumerable<TextSpan> UncommentedSpansInSelection { get; }

            public BlockCommentSelectionHelper(IEnumerable<SyntaxTrivia> allBlockComments, SourceText sourceText, TextSpan selectedSpan)
            {
                sourceText = sourceText;
                _text = sourceText.ToString(selectedSpan).Trim();

                SelectedSpan = selectedSpan;
                IntersectingBlockComments = GetIntersectingBlockComments(allBlockComments, selectedSpan);
                UncommentedSpansInSelection = GetUncommentedSpansInSelection();
            }

            /// <summary>
            /// Determines if the given span is entirely whitespace.
            /// </summary>
            /// <param name="span">the span to check for whitespace.</param>
            /// <returns>true if the span is entirely whitespace.</returns>
            public bool IsSpanWhitespace(TextSpan span)
            {
                var text = _sourceText.GetSubText(span).ToString();
                for (var i = 0; i < text.Length; i++)
                {
                    var character = text[i];
                    if (!IsLocationWhitespace(character))
                    {
                        return false;
                    }
                }

                return true;

                bool IsLocationWhitespace(char character)
                {
                    return SyntaxFacts.IsWhitespace(character) || SyntaxFacts.IsNewLine(character);
                }
            }

            /// <summary>
            /// Determines if the location falls inside a commented span.
            /// </summary>
            public bool IsLocationCommented(int location)
            {
                return IntersectingBlockComments.Contains(comment => comment.Span.Contains(location));
            }

            public bool DoesBeginWithBlockComment(string blockCommentStartString, string blockCommentEndString)
            {
                return _text.StartsWith(blockCommentStartString, StringComparison.Ordinal)
                       || _text.StartsWith(blockCommentEndString, StringComparison.Ordinal);
            }

            public bool DoesEndWithBlockComment(string blockCommentStartString, string blockCommentEndString)
            {
                return _text.EndsWith(blockCommentStartString, StringComparison.Ordinal)
                       || _text.EndsWith(blockCommentEndString, StringComparison.Ordinal);
            }

            /// <summary>
            /// Checks if the selected span contains any uncommented non whitespace characters.
            /// </summary>
            public bool IsEntirelyCommented()
            {
                return !UncommentedSpansInSelection.Any() && HasIntersectingBlockComments();
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
                return _sourceText.GetSubText(new TextSpan(position, length)).ToString();
            }

            private bool AreOnSameLine(int positionOne, int positionTwo)
            {
                var line1 = _sourceText.Lines.GetLineFromPosition(positionOne);
                var line2 = _sourceText.Lines.GetLineFromPosition(positionTwo);
                return line1.LineNumber == line2.LineNumber;
            }

            /// <summary>
            /// Tries to get a block comment on the same line.  There are two cases:
            ///     1.  The caret is preceding a block comment on the same line, with only whitespace before the comment.
            ///     2.  The caret is following a block comment on the same line, with only whitespace after the comment.
            /// </summary>
            public bool TryGetBlockCommentOnSameLine(IEnumerable<SyntaxTrivia> allBlockComments, out SyntaxTrivia commentedOnSameLine)
            {
                var selectedLine = _sourceText.Lines.GetLineFromPosition(SelectedSpan.Start);
                var lineStartToCaretIsWhitespace = IsSpanWhitespace(TextSpan.FromBounds(selectedLine.Start, SelectedSpan.Start));
                var caretToLineEndIsWhitespace = IsSpanWhitespace(TextSpan.FromBounds(SelectedSpan.Start, selectedLine.End));
                foreach (var blockComment in allBlockComments)
                {
                    var blockCommentSpan = blockComment.Span;
                    if (lineStartToCaretIsWhitespace && SelectedSpan.Start < blockCommentSpan.Start && AreOnSameLine(SelectedSpan.Start, blockCommentSpan.Start))
                    {
                        if (IsSpanWhitespace(TextSpan.FromBounds(SelectedSpan.Start, blockCommentSpan.Start)))
                        {
                            commentedOnSameLine = blockComment;
                            return true;
                        }
                    }
                    else if (caretToLineEndIsWhitespace && SelectedSpan.Start > blockCommentSpan.End && AreOnSameLine(SelectedSpan.Start, blockCommentSpan.End))
                    {
                        if (IsSpanWhitespace(TextSpan.FromBounds(blockCommentSpan.End, SelectedSpan.Start)))
                        {
                            commentedOnSameLine = blockComment;
                            return true;
                        }
                    }
                }

                commentedOnSameLine = new SyntaxTrivia();
                return false;
            }

            /// <summary>
            /// Gets a list of block comments that intersect the span.
            /// Spans are intersecting if 1 location is the same between them (empty spans look at the start).
            /// </summary>
            private IEnumerable<SyntaxTrivia> GetIntersectingBlockComments(IEnumerable<SyntaxTrivia> allBlockComments, TextSpan span)
            {
                return allBlockComments.Where(blockComment => span.OverlapsWith(blockComment.Span) || blockComment.Span.Contains(span));
            }

            /// <summary>
            /// Retrieves all non commented, non whitespace spans.
            /// </summary>
            private IEnumerable<TextSpan> GetUncommentedSpansInSelection()
            {
                var uncommentedSpans = new List<TextSpan>();

                // Invert the commented spans to get the uncommented spans.
                int spanStart = SelectedSpan.Start;
                foreach (var comment in IntersectingBlockComments)
                {
                    var commentedSpan = comment.Span;
                    if (commentedSpan.Start > spanStart)
                    {
                        // Get span up until the comment and check to make sure it is not whitespace.
                        var possibleUncommentedSpan = TextSpan.FromBounds(spanStart, commentedSpan.Start);
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
                    var uncommentedSpan = TextSpan.FromBounds(spanStart, SelectedSpan.End);
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
