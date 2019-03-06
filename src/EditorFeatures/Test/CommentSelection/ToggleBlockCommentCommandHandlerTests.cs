// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CommentSelection;
using Microsoft.CodeAnalysis.Editor.Implementation.CommentSelection;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.UnitTests.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Text.Shared.Extensions;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Roslyn.Test.EditorUtilities;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.UnitTests.CommentSelection
{
    [UseExportProvider]
    public class ToggleBlockCommentCommandHandlerTests
    {
        private class MockCommentSelectionService : AbstractCommentSelectionService
        {
            public MockCommentSelectionService()
            {
                SupportsBlockComment = true;
            }

            public override string SingleLineCommentString => "//";
            public override bool SupportsBlockComment { get; }
            public override string BlockCommentStartString => "/*";
            public override string BlockCommentEndString => "*/";
        }

        [Fact, Trait(Traits.Feature, Traits.Features.ToggleBlockComments)]
        public void Create()
        {
            Assert.NotNull(new MockCommentSelectionService());
        }

        [WpfFact, Trait(Traits.Feature, Traits.Features.ToggleBlockComments)]
        public void Comment_EmptyLine()
        {
            var code = @"|start||end|";
            ToggleBlockComment(code, Enumerable.Empty<TextChange>(), null);
        }

        [WpfFact, Trait(Traits.Feature, Traits.Features.ToggleBlockComments)]
        public void Comment_ApplyTwice()
        {
            var code = @"|start|class C
{
    void M() { }
}|end|
";
            using (var disposableView = EditorFactory.CreateView(TestExportProvider.ExportProviderWithCSharpAndVisualBasic, code))
            {
                var selectedSpans = SetupSelection(disposableView.TextView);

                var expectedChanges = new[]
                {
                new TextChange(new TextSpan(0, 0), "//"),
                new TextChange(new TextSpan(9, 0), "//"),
                new TextChange(new TextSpan(12, 0), "//"),
                new TextChange(new TextSpan(30, 0), "//"),
            };
                ToggleBlockComment(
                    disposableView.TextView,
                    expectedChanges,
                    expectedSelectedSpans: new[] { new Span(0, 39) });

                expectedChanges = new[]
                {
                new TextChange(new TextSpan(0, 0), "//"),
                new TextChange(new TextSpan(11, 0), "//"),
                new TextChange(new TextSpan(16, 0), "//"),
                new TextChange(new TextSpan(36, 0), "//"),
            };
                ToggleBlockComment(
                    disposableView.TextView,
                    expectedChanges,
                    expectedSelectedSpans: new[] { new Span(0, 47) });
            }
        }

        [WpfFact, Trait(Traits.Feature, Traits.Features.ToggleBlockComments)]
        public void Uncomment_BoxSelection()
        {
            var code = @"
class Goo
{
    |start|/*v*/|end|oid M()
    |start|//{  |end|
    |start|/*o*/|end|ther
    |start|//}  |end|
}";

            var expectedChanges = new[]
            {
                new TextChange(new TextSpan(20, 2), string.Empty),
                new TextChange(new TextSpan(23, 2), string.Empty),
                new TextChange(new TextSpan(38, 2), string.Empty),
                new TextChange(new TextSpan(49, 2), string.Empty),
                new TextChange(new TextSpan(52, 2), string.Empty),
                new TextChange(new TextSpan(64, 2), string.Empty),
            };

            var expectedSelectedSpans = new[]
                {
                    Span.FromBounds(20, 21)
                 };

            ToggleBlockComment(code, expectedChanges, expectedSelectedSpans);
        }

        [WpfFact, Trait(Traits.Feature, Traits.Features.ToggleBlockComments)]
        public void Uncomment_PartOfMultipleComments()
        {
            var code = @"
//|start|//namespace N
////{
//|end|//}";

            var expectedChanges = new[]
            {
                new TextChange(new TextSpan(2, 2), string.Empty),
                new TextChange(new TextSpan(19, 2), string.Empty),
                new TextChange(new TextSpan(26, 2), string.Empty),
            };
            ToggleBlockComment(code, expectedChanges, Span.FromBounds(2, 25));
        }

        [WpfFact, Trait(Traits.Feature, Traits.Features.ToggleBlockComments)]
        [WorkItem(932411, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/932411")]
        public void Uncomment_BlockCommentWithNoEnd()
        {
            var code = @"/*using |start||end|System;";
            ToggleBlockComment(code, Enumerable.Empty<TextChange>(), new Span(8, 0));
        }

        [WpfFact, Trait(Traits.Feature, Traits.Features.ToggleBlockComments)]
        [WorkItem(31669, "https://github.com/dotnet/roslyn/issues/31669")]
        public void Uncomment_BlockWithSingleInside()
        {
            var code = @"
class A
{
    |start|/*
    void M()
    {
            // A comment
            // Another comment
    }
    */|end|
}";

            var expectedChanges = new[]
            {
                new TextChange(new TextSpan(18, 2), string.Empty),
                new TextChange(new TextSpan(112, 2), string.Empty),
            };

            var expectedSelectedSpans = new[]
            {
                Span.FromBounds(18, 110)
            };

            ToggleBlockComment(code, expectedChanges, expectedSelectedSpans);
        }

        [WpfFact, Trait(Traits.Feature, Traits.Features.ToggleBlockComments)]
        [WorkItem(31669, "https://github.com/dotnet/roslyn/issues/31669")]
        public void Uncomment_SingleLinesWithBlockAndSingleInside()
        {
            var code = @"
class A
{
    |start|///*
    //void M()
    //{
    //     // A comment
    //     // Another comment
    //}
    //*/|end|
}";

            var expectedChanges = new[]
            {
                new TextChange(new TextSpan(18, 2), string.Empty),
                new TextChange(new TextSpan(28, 2), string.Empty),
                new TextChange(new TextSpan(44, 2), string.Empty),
                new TextChange(new TextSpan(53, 2), string.Empty),
                new TextChange(new TextSpan(78, 2), string.Empty),
                new TextChange(new TextSpan(109, 2), string.Empty),
                new TextChange(new TextSpan(118, 2), string.Empty),
            };

            var expectedSelectedSpans = new[]
            {
                Span.FromBounds(14, 108)
            };

            ToggleBlockComment(code, expectedChanges, expectedSelectedSpans);
        }

        private static void ToggleBlockComment(string code, IEnumerable<TextChange> expectedChanges, Span expectedSelectedSpan)
        {
            ToggleBlockComment(code, expectedChanges, new List<Span> { expectedSelectedSpan });
        }

        private static void ToggleBlockComment(string code, IEnumerable<TextChange> expectedChanges, IEnumerable<Span> expectedSelectedSpans)
        {
            using (var disposableView = EditorFactory.CreateView(TestExportProvider.ExportProviderWithCSharpAndVisualBasic, code))
            {
                var selectedSpans = SetupSelection(disposableView.TextView);

                ToggleBlockComment(disposableView.TextView, expectedChanges, expectedSelectedSpans);
            }
        }

        private static void ToggleBlockComment(
            ITextView textView,
            IEnumerable<TextChange> expectedChanges,
            IEnumerable<Span> expectedSelectedSpans)
        {
            var textUndoHistoryRegistry = TestExportProvider.ExportProviderWithCSharpAndVisualBasic.GetExportedValue<ITextUndoHistoryRegistry>();
            var editorOperationsFactory = TestExportProvider.ExportProviderWithCSharpAndVisualBasic.GetExportedValue<IEditorOperationsFactoryService>();
            var commandHandler = new ToggleBlockCommentCommandHandler(textUndoHistoryRegistry, editorOperationsFactory);
            var service = new MockCommentSelectionService();

            var trackingSpans = new Dictionary<ITrackingSpan, Operation>();
            var textChanges = new List<TextChange>();

            commandHandler.CollectEdits(
                null, service, textView.Selection.GetSnapshotSpansOnBuffer(textView.TextBuffer),
                textChanges, trackingSpans, CancellationToken.None);

            Roslyn.Test.Utilities.AssertEx.SetEqual(expectedChanges, textChanges);

            // Actually apply the edit to let the tracking spans adjust.
            using (var edit = textView.TextBuffer.CreateEdit())
            {
                textChanges.Do(tc => edit.Replace(tc.Span.ToSpan(), tc.NewText));

                edit.Apply();
            }

            if (trackingSpans.Any())
            {
                textView.SetSelection(trackingSpans.First().Key.GetSpan(textView.TextSnapshot));
            }

            if (expectedSelectedSpans != null)
            {
                Roslyn.Test.Utilities.AssertEx.Equal(expectedSelectedSpans, textView.Selection.SelectedSpans.Select(snapshotSpan => snapshotSpan.Span));
            }
        }

        private static IEnumerable<Span> SetupSelection(IWpfTextView textView)
        {
            var spans = new List<Span>();
            while (true)
            {
                var startOfSelection = FindAndRemoveMarker(textView, "|start|");
                var endOfSelection = FindAndRemoveMarker(textView, "|end|");

                if (startOfSelection < 0)
                {
                    break;
                }
                else
                {
                    spans.Add(Span.FromBounds(startOfSelection, endOfSelection));
                }
            }

            var snapshot = textView.TextSnapshot;
            if (spans.Count == 1)
            {
                textView.Selection.Select(new SnapshotSpan(snapshot, spans.Single()), isReversed: false);
                textView.Caret.MoveTo(new SnapshotPoint(snapshot, spans.Single().End));
            }
            else
            {
                textView.Selection.Mode = TextSelectionMode.Box;
                textView.Selection.Select(new VirtualSnapshotPoint(snapshot, spans.First().Start),
                                          new VirtualSnapshotPoint(snapshot, spans.Last().End));
                textView.Caret.MoveTo(new SnapshotPoint(snapshot, spans.Last().End));
            }

            return spans;
        }

        private static int FindAndRemoveMarker(ITextView textView, string marker)
        {
            var index = textView.TextSnapshot.GetText().IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                textView.TextBuffer.Delete(new Span(index, marker.Length));
            }

            return index;
        }
    }
}
