// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Text;
using LSP = Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer
{
    internal static class ProtocolConversions
    {
        public static Uri UriFromDocument(Document document)
        {
            return new Uri(document.FilePath);
        }

        public static LSP.Position LinePositionToPosition(LinePosition linePosition)
        {
            return new LSP.Position { Line = linePosition.Line, Character = linePosition.Character };
        }

        public static LSP.Range LinePositionToRange(LinePositionSpan linePositionSpan)
        {
            return new LSP.Range { Start = LinePositionToPosition(linePositionSpan.Start), End = LinePositionToPosition(linePositionSpan.End) };
        }

        public static LSP.Range TextSpanToRange(TextSpan textSpan, SourceText text)
        {
            var linePosSpan = text.Lines.GetLinePositionSpan(textSpan);
            return LinePositionToRange(linePosSpan);
        }

        public static LinePosition PositionToLinePosition(LSP.Position position)
        {
            return new LinePosition(position.Line, position.Character);
        }

        public static Task<LSP.Location> DocumentSpanToLocationAsync(DocumentSpan documentSpan, CancellationToken cancellationToken)
        {
            return TextSpanToLocationAsync(documentSpan.Document, documentSpan.SourceSpan, cancellationToken);
        }

        public static async Task<LSP.Location> TextSpanToLocationAsync(Document document, TextSpan span, CancellationToken cancellationToken)
        {
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);

            var location = new LSP.Location
            {
                Uri = UriFromDocument(document),
                Range = TextSpanToRange(span, text),
            };

            return location;
        }

        public static LSP.SymbolKind GlyphToSymbolKind(Glyph glyph)
        {
            var glyphString = glyph.ToString().Replace("Public", string.Empty)
                                              .Replace("Protected", string.Empty)
                                              .Replace("Private", string.Empty)
                                              .Replace("Internal", string.Empty);

            if (Enum.TryParse<LSP.SymbolKind>(glyphString, out var symbolKind))
            {
                return symbolKind;
            }

            switch (glyph)
            {
                case Glyph.Assembly:
                case Glyph.BasicProject:
                case Glyph.CSharpProject:
                case Glyph.NuGet:
                    return LSP.SymbolKind.Package;
                case Glyph.BasicFile:
                case Glyph.CSharpFile:
                    return LSP.SymbolKind.File;
                case Glyph.DelegatePublic:
                case Glyph.DelegateProtected:
                case Glyph.DelegatePrivate:
                case Glyph.DelegateInternal:
                    return LSP.SymbolKind.Function;
                case Glyph.ExtensionMethodPublic:
                case Glyph.ExtensionMethodProtected:
                case Glyph.ExtensionMethodPrivate:
                case Glyph.ExtensionMethodInternal:
                    return LSP.SymbolKind.Method;
                case Glyph.Local:
                case Glyph.Parameter:
                case Glyph.RangeVariable:
                case Glyph.Reference:
                    return LSP.SymbolKind.Variable;
                case Glyph.StructurePublic:
                case Glyph.StructureProtected:
                case Glyph.StructurePrivate:
                case Glyph.StructureInternal:
                    return LSP.SymbolKind.Struct;
                default:
                    return LSP.SymbolKind.Object;
            }
        }
    }
}
