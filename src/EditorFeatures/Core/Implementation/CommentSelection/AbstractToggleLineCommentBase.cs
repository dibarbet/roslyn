// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CommentSelection;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Experiments;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Text.Shared.Extensions;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Text.Operations;
using Roslyn.Utilities;
using VSCommanding = Microsoft.VisualStudio.Commanding;

namespace Microsoft.CodeAnalysis.Editor.Implementation.CommentSelection
{
    internal abstract class AbstractToggleLineCommentBase :
        // Value tuple to represent that there is no distinct command to be passed in.
        AbstractCommentSelectionBase<ValueTuple>,
        VSCommanding.ICommandHandler<ToggleLineCommentCommandArgs>
    {
        private static readonly CommentSelectionResult s_emptyCommentSelectionResult =
            new CommentSelectionResult(new List<TextChange>(), new List<CommentTrackingSpan>(), Operation.Uncomment);

        internal AbstractToggleLineCommentBase(
            ITextUndoHistoryRegistry undoHistoryRegistry,
            IEditorOperationsFactoryService editorOperationsFactoryService)
            : base(undoHistoryRegistry, editorOperationsFactoryService)
        {
        }

        public VSCommanding.CommandState GetCommandState(ToggleLineCommentCommandArgs args)
        {
            if (Workspace.TryGetWorkspace(args.SubjectBuffer.AsTextContainer(), out var workspace))
            {
                var experimentationService = workspace.Services.GetRequiredService<IExperimentationService>();
                if (!experimentationService.IsExperimentEnabled(WellKnownExperimentNames.RoslynToggleBlockComment))
                {
                    return VSCommanding.CommandState.Unspecified;
                }
            }
            return GetCommandState(args.SubjectBuffer);
        }

        public bool ExecuteCommand(ToggleLineCommentCommandArgs args, CommandExecutionContext context)
        {
            return ExecuteCommand(args.TextView, args.SubjectBuffer, ValueTuple.Create(), context);
        }

        public override string DisplayName => EditorFeaturesResources.Toggle_Block_Comment;

        protected override string GetTitle(ValueTuple command) => EditorFeaturesResources.Toggle_Block_Comment;

        protected override string GetMessage(ValueTuple command) => EditorFeaturesResources.Toggling_block_comment;

        internal async override Task<CommentSelectionResult> CollectEdits(Document document, ICommentSelectionService service,
            ITextBuffer subjectBuffer, NormalizedSnapshotSpanCollection selectedSpans, ValueTuple command, CancellationToken cancellationToken)
        {
            var experimentationService = document.Project.Solution.Workspace.Services.GetRequiredService<IExperimentationService>();
            if (!experimentationService.IsExperimentEnabled(WellKnownExperimentNames.RoslynToggleBlockComment))
            {
                return s_emptyCommentSelectionResult;
            }

            var commentInfo = await service.GetInfoAsync(document, selectedSpans.First().Span.ToTextSpan(), cancellationToken).ConfigureAwait(false);
            if (commentInfo.SupportsSingleLineComment)
            {
                return await ToggleLineComment(document, commentInfo, selectedSpans, cancellationToken).ConfigureAwait(false);
            }

            return s_emptyCommentSelectionResult;
        }

        /// <summary>
        /// Given a span, find the first and last line that are part of the span.  NOTE: If the 
        /// span ends in column zero, we back up to the previous line, to handle the case where 
        /// the user used shift + down to select a bunch of lines.  They probably don't want the 
        /// last line commented in that case.
        /// </summary>
        private static (ITextSnapshotLine firstLine, ITextSnapshotLine lastLine) DetermineFirstAndLastLine(SnapshotSpan span)
        {
            var firstLine = span.Snapshot.GetLineFromPosition(span.Start.Position);
            var lastLine = span.Snapshot.GetLineFromPosition(span.End.Position);
            if (lastLine.Start == span.End.Position && !span.IsEmpty)
            {
                lastLine = lastLine.GetPreviousMatchingLine(_ => true);
            }

            return (firstLine, lastLine);
        }

        private async Task<CommentSelectionResult> ToggleLineComment(Document document, CommentSelectionInfo commentInfo,
            NormalizedSnapshotSpanCollection selectedSpans, CancellationToken cancellationToken)
        {
            foreach (var selection in selectedSpans)
            {
                // Get line(s) selected

                // If any line is commented, uncomment.

                // Otherwise add comment for all lines.

                // Uncomment if line commented.

                // Comment if line uncommented.
            }




        }
    }
}
