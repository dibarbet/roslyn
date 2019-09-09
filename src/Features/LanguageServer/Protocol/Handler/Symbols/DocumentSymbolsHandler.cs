// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Editor;
using Microsoft.CodeAnalysis.Editor.Extensibility.NavigationBar;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Roslyn.Utilities;
using LSP = Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler
{
    [Shared]
    [ExportLspMethod(Methods.TextDocumentDocumentSymbolName)]
    internal class DocumentSymbolsHandler : IRequestHandler<DocumentSymbolParams, object[]>
    {
        public async Task<object[]> HandleRequestAsync(Solution solution, DocumentSymbolParams request,
            ClientCapabilities clientCapabilities, CancellationToken cancellationToken, bool keepThreadContext = false)
        {
            using var symbolDisposer = ArrayBuilder<object>.GetInstance(out var symbols);

            var document = solution.GetDocumentFromURI(request.TextDocument.Uri);
            if (document == null)
            {
                return symbols.ToArray();
            }

            var navBarService = document.Project.LanguageServices.GetService<INavigationBarItemService>();
            if (navBarService == null)
            {
                return symbols.ToArray();
            }

            var navBarItems = await navBarService.GetItemsAsync(document, cancellationToken).ConfigureAwait(keepThreadContext);
            if (navBarItems.Count == 0)
            {
                return symbols.ToArray();
            }

            var compilation = await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(keepThreadContext);
            var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(keepThreadContext);
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(keepThreadContext);

            var symbolItems = GetSymbolItems(navBarItems);

            // TODO - Return more than 2 levels of symbols.
            // https://github.com/dotnet/roslyn/projects/45#card-20033869
            if (clientCapabilities?.TextDocument?.DocumentSymbol?.HierarchicalDocumentSymbolSupport == true)
            {
                foreach (var item in symbolItems)
                {
                    // only top level ones
                    symbols.Add(await GetDocumentSymbolAsync(item, compilation, tree, text, keepThreadContext, cancellationToken).ConfigureAwait(keepThreadContext));
                }
            }
            else
            {
                foreach (var item in symbolItems)
                {
                    symbols.Add(GetSymbolInformation(item, compilation, tree, document, text, cancellationToken, containerName: null));

                    foreach (var childItem in GetSymbolItems(item.ChildItems))
                    {
                        symbols.Add(GetSymbolInformation(childItem, compilation, tree, document, text, cancellationToken, item.Text));
                    }
                }
            }

            var result = symbols.WhereNotNull().ToArray();
            return result;
        }

        private static IEnumerable<NavigationBarSymbolItem> GetSymbolItems(IEnumerable<NavigationBarItem> navBarItems)
            => navBarItems.Where(item => item is NavigationBarSymbolItem).Select(item => (NavigationBarSymbolItem)item);

        /// <summary>
        /// Get a symbol information from a specified nav bar item.
        /// </summary>
        private static SymbolInformation GetSymbolInformation(NavigationBarSymbolItem item, Compilation compilation, SyntaxTree tree, Document document,
            SourceText text, CancellationToken cancellationToken, string containerName = null)
        {
            if (item.Spans.Count == 0 || !TryGetSymbolForNavigationBarItem(item, compilation, cancellationToken, out var symbol))
            {
                return null;
            }

            var location = GetLocation(symbol, tree);

            return new SymbolInformation
            {
                Name = item.Text,
                Location = new LSP.Location
                {
                    Uri = document.GetURI(),
                    Range = ProtocolConversions.TextSpanToRange(location.SourceSpan, text),
                },
                Kind = ProtocolConversions.GlyphToSymbolKind(item.Glyph),
                ContainerName = containerName,
            };
        }

        /// <summary>
        /// Get a document symbol from a specified nav bar item.
        /// </summary>
        private static DocumentSymbol GetDocumentSymbolAsync(NavigationBarSymbolItem item, Compilation compilation, SyntaxTree tree,
            SourceText text, bool keepThreadContext, CancellationToken cancellationToken)
        {
            if (item.Spans.Count == 0 || !TryGetSymbolForNavigationBarItem(item, compilation, cancellationToken, out var symbol))
            {
                return null;
            }

            // TODO - CHECK MATCHES BEFORE REPLACE.
            string n = nameof(ObsoleteAttribute);

            var location = GetLocation(symbol, tree);

            symbol.Kind

            return new DocumentSymbol
            {
                Name = symbol.Name,
                Detail = item.Text,
                Kind = ProtocolConversions.GlyphToSymbolKind(item.Glyph),
                Deprecated = symbol.GetAttributes().Any(x => x.AttributeClass.MetadataName == "ObsoleteAttribute"),
                Range = ProtocolConversions.TextSpanToRange(item.Spans.First(), text),
                SelectionRange = ProtocolConversions.TextSpanToRange(location.SourceSpan, text),
                Children = GetChildrenAsync(GetSymbolItems(item.ChildItems), compilation, tree, text, keepThreadContext, cancellationToken),
            };

            static DocumentSymbol[] GetChildrenAsync(IEnumerable<NavigationBarSymbolItem> items, Compilation compilation, SyntaxTree tree,
                SourceText text, bool keepThreadContext, CancellationToken cancellationToken)
            {
                using var childSymbolsDisposer = ArrayBuilder<DocumentSymbol>.GetInstance(out var list);
                foreach (var item in items)
                {
                    list.Add(GetDocumentSymbolAsync(item, compilation, tree, text, keepThreadContext, cancellationToken));
                }

                return list.ToArray();
            }
        }

        /// <summary>
        /// Gets the symbol associated with a nav bar item.
        /// </summary>
        private static bool TryGetSymbolForNavigationBarItem(NavigationBarSymbolItem item, Compilation compilation, CancellationToken cancellationToken, out ISymbol symbol)
        {
            var symbols = item.NavigationSymbolId.Resolve(compilation, cancellationToken: cancellationToken);
            symbol = symbols.Symbol;
            if (symbol != null)
            {
                return true;
            }
            else if (item.NavigationSymbolIndex < symbols.CandidateSymbols.Length)
            {
                symbol = symbols.CandidateSymbols[item.NavigationSymbolIndex.Value];
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Get a location for a particular nav bar item.
        /// </summary>
        private static Location GetLocation(ISymbol symbol, SyntaxTree tree)
        {
            var location = symbol.Locations.FirstOrDefault(l => l.SourceTree.Equals(tree));
            return location ?? symbol.Locations.FirstOrDefault();
        }
    }
}
