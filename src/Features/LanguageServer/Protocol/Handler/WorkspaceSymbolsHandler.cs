// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.NavigateTo;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer
{
    internal static class WorkspaceSymbolsHandler
    {
        internal static async Task<SymbolInformation[]> GetWorkspaceSymbolsAsync(Solution solution, WorkspaceSymbolParams request, CancellationToken cancellationToken)
        {
            var symbols = ArrayBuilder<SymbolInformation>.GetInstance();

            var searchTasks = solution.Projects.Select(
                p => Task.Run(() => SearchProjectAsync(p, request, symbols, cancellationToken), cancellationToken)).ToArray();

            await Task.WhenAll(searchTasks).ConfigureAwait(false);

            return symbols.ToArrayAndFree();

            // local functions
            static async Task SearchProjectAsync(Project project, WorkspaceSymbolParams request,
                ArrayBuilder<SymbolInformation> symbols, CancellationToken cancellationToken)
            {
                var searchService = project.LanguageServices.GetService<INavigateToSearchService_RemoveInterfaceAboveAndRenameThisAfterInternalsVisibleToUsersUpdate>();
                if (searchService != null)
                {
                    // TODO - Update Kinds Provided to return all necessary symbols.
                    // https://github.com/dotnet/roslyn/projects/45#card-20033822
                    var items = await searchService.SearchProjectAsync(
                        project,
                        ImmutableArray<Document>.Empty,
                        request.Query,
                        searchService.KindsProvided,
                        cancellationToken).ConfigureAwait(false);

                    foreach (var item in items)
                    {
                        var symbolInfo = new SymbolInformation
                        {
                            Name = item.Name,
                            Kind = item.Kind.GetKind(),
                            Location = await ProtocolConversions.TextSpanToLocationAsync(item.NavigableItem.Document, item.NavigableItem.SourceSpan, cancellationToken).ConfigureAwait(false),
                        };

                        lock (symbols)
                        {
                            symbols.Add(symbolInfo);
                        }
                    }
                }
            }
        }
    }
}
