// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.OrganizeImports;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Text;
using LSP = Roslyn.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler;

internal abstract class AbstractFormatDocumentHandlerBase<RequestType, ResponseType> : ILspServiceDocumentRequestHandler<RequestType, ResponseType>
{
    public bool MutatesSolutionState => false;
    public bool RequiresLSPSolution => true;

    protected static async Task<LSP.TextEdit[]?> GetTextEditsAsync(
        RequestContext context,
        LSP.FormattingOptions options,
        IGlobalOptionService globalOptions,
        CancellationToken cancellationToken,
        LSP.Range? range = null)
    {
        if (context.Document is not { } document)
            return null;

        var text = await document.GetValueTextAsync(cancellationToken).ConfigureAwait(false);
        var root = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

        var rangeSpan = (range != null) ? ProtocolConversions.RangeToTextSpan(range, text) : new TextSpan(0, root.FullSpan.Length);
        var formattingSpan = CommonFormattingHelpers.GetFormattingSpan(root, rangeSpan);

        // We should use the options passed in by LSP instead of the document's options.
        var formattingOptions = await ProtocolConversions.GetFormattingOptionsAsync(options, document, cancellationToken).ConfigureAwait(false);
        var services = document.Project.Solution.Services;
        var formattingChanges = Formatter.GetFormattedTextChanges(root, SpecializedCollections.SingletonEnumerable(formattingSpan), services, formattingOptions, cancellationToken);

        // Formatting must produce non-overlapping changes; otherwise downstream consumers (e.g. SourceText.WithChanges
        // below, or the LSP client applying the resulting TextEdits) will throw an opaque ArgumentException
        // ("The changes must not overlap.").  We've had several reports of this happening in the wild without a
        // reproducer (see https://github.com/dotnet/vscode-csharp/issues/9341).  Detect the situation here and log
        // the offending changes (including the source snippet that surrounds them) so future reports include enough
        // information to construct a failing test.  This only fires when trace logging is enabled and the user has
        // already opted into sharing diagnostic logs.
        LogOverlappingFormattingChanges(context, document, text, formattingChanges);

        // We only organize the imports when formatting the entire document. This means we can stop
        // if we are provided a range or sorting imports is disabled/
        if (range is not null || !globalOptions.GetOption(LspOptionsStorage.LspOrganizeImportsOnFormat, document.Project.Language))
        {
            return [.. formattingChanges.Select(change => ProtocolConversions.TextChangeToTextEdit(change, text))];
        }

        var formattedDocument = document.WithText(text.WithChanges(formattingChanges));

        var organizeImports = formattedDocument.GetRequiredLanguageService<IOrganizeImportsService>();
        var organizeImportsOptions = await formattedDocument.GetOrganizeImportsOptionsAsync(cancellationToken).ConfigureAwait(false);
        var organizedDocument = await organizeImports.OrganizeImportsAsync(formattedDocument, organizeImportsOptions, cancellationToken).ConfigureAwait(false);

        var textChanges = await organizedDocument.GetTextChangesAsync(context.Document).ConfigureAwait(false);
        return [.. textChanges.Select(change => ProtocolConversions.TextChangeToTextEdit(change, text))];
    }

    public abstract LSP.TextDocumentIdentifier GetTextDocumentIdentifier(RequestType request);
    public abstract Task<ResponseType> HandleRequestAsync(RequestType request, RequestContext context, CancellationToken cancellationToken);

    private static void LogOverlappingFormattingChanges(RequestContext context, Document document, SourceText text, IList<TextChange> formattingChanges)
    {
        if (formattingChanges.Count < 2)
            return;

        // Order by span start so we only need to compare adjacent entries.  We don't mutate the original list.
        var ordered = formattingChanges.OrderBy(c => c.Span.Start).ThenBy(c => c.Span.End).ToArray();
        for (var i = 1; i < ordered.Length; i++)
        {
            var previous = ordered[i - 1];
            var current = ordered[i];
            if (current.Span.Start < previous.Span.End)
            {
                // Log every overlapping pair so the user can see the full picture.  We include both the formatter-
                // produced NewText (whitespace) and a snippet of the original source covering the overlap region
                // plus a small context window.  This is necessary to identify which syntactic construct caused the
                // formatter to produce overlapping changes -- whitespace alone is not enough.
                var overlapStart = previous.Span.Start;
                var overlapEnd = System.Math.Max(previous.Span.End, current.Span.End);
                var snippet = GetSnippetWithContext(text, TextSpan.FromBounds(overlapStart, overlapEnd));
                var startLine = text.Lines.GetLinePosition(overlapStart);
                var endLine = text.Lines.GetLinePosition(System.Math.Min(overlapEnd, text.Length));

                context.TraceError(
                    $"Formatting produced overlapping text changes for '{document.FilePath ?? document.Name}' at " +
                    $"({startLine.Line + 1},{startLine.Character + 1})-({endLine.Line + 1},{endLine.Character + 1}). " +
                    $"Previous change span=[{previous.Span.Start}, {previous.Span.End}) newText={EscapeForLog(previous.NewText)}; " +
                    $"current change span=[{current.Span.Start}, {current.Span.End}) newText={EscapeForLog(current.NewText)}. " +
                    $"Source snippet (with context): {EscapeForLog(snippet)}. " +
                    $"Please report this with the file you were formatting at https://github.com/dotnet/roslyn/issues.");
            }
        }
    }

    private static string GetSnippetWithContext(SourceText text, TextSpan span)
    {
        // Expand to whole-line boundaries and include one line of context on either side so the snippet is
        // easier to read and reason about.
        var startLineIndex = System.Math.Max(0, text.Lines.GetLineFromPosition(span.Start).LineNumber - 1);
        var endLineIndex = System.Math.Min(text.Lines.Count - 1, text.Lines.GetLineFromPosition(System.Math.Min(span.End, text.Length)).LineNumber + 1);
        var snippetSpan = TextSpan.FromBounds(text.Lines[startLineIndex].Start, text.Lines[endLineIndex].EndIncludingLineBreak);

        // Cap the snippet length so a pathological input can't produce an enormous log entry.
        const int MaxSnippetLength = 2048;
        if (snippetSpan.Length > MaxSnippetLength)
            snippetSpan = new TextSpan(snippetSpan.Start, MaxSnippetLength);

        return text.ToString(snippetSpan);
    }

    private static string EscapeForLog(string? text)
    {
        if (text is null)
            return "<null>";

        return "\"" + text
            .Replace("\\", "\\\\")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t")
            .Replace("\"", "\\\"") + "\"";
    }
}
