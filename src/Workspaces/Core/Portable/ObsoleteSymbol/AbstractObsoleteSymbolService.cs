// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.LanguageService;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.ObsoleteSymbol;

internal abstract class AbstractObsoleteSymbolService(int? dimKeywordKind) : IObsoleteSymbolService
{
    private readonly int? _dimKeywordKind = dimKeywordKind;

    protected virtual void ProcessDimKeyword(ArrayBuilder<TextSpan> result, SemanticModel semanticModel, SyntaxToken token, CancellationToken cancellationToken)
    {
        // Take no action by default
    }

    public async Task<ImmutableArray<TextSpan>> GetLocationsAsync(Document document, ImmutableArray<TextSpan> textSpans, CancellationToken cancellationToken)
    {
        var syntaxFacts = document.GetRequiredLanguageService<ISyntaxFactsService>();

        var compilation = await document.Project.GetRequiredCompilationAsync(cancellationToken).ConfigureAwait(false);
        var root = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

        using var _2 = ArrayBuilder<TextSpan>.GetInstance(out var result);
        var semanticModel = compilation.GetSemanticModel(root.SyntaxTree);

        foreach (var span in textSpans)
        {
            Recurse(span, semanticModel);
        }

        result.RemoveDuplicates();
        return result.ToImmutableAndClear();

        void Recurse(TextSpan span, SemanticModel semanticModel)
        {
            using var _ = ArrayBuilder<SyntaxNode>.GetInstance(out var stack);

            // Walk through all the nodes in the provided span.  Directly analyze local or parameter declaration.  And
            // also analyze any identifiers which might be reference to locals or parameters.  Note that we might hit
            // locals/parameters without any references in the span, or references that don't have the declarations in 
            // the span
            stack.Add(root.FindNode(span));

            // Use a stack so we don't blow out the stack with recursion.
            while (stack.Count > 0)
            {
                var current = stack.Last();
                stack.RemoveLast();

                if (current.Span.IntersectsWith(span))
                {
                    var tokenFromNode = ProcessNode(semanticModel, current);

                    foreach (var child in current.ChildNodesAndTokens())
                    {
                        if (child.IsNode)
                        {
                            stack.Add(child.AsNode()!);
                        }

                        var token = child.AsToken();
                        if (token != tokenFromNode)
                            ProcessToken(semanticModel, child.AsToken());

                        foreach (var trivia in token.LeadingTrivia)
                        {
                            if (trivia.HasStructure)
                            {
                                stack.Add(trivia.GetStructure()!);
                            }
                        }

                        foreach (var trivia in token.TrailingTrivia)
                        {
                            if (trivia.HasStructure)
                            {
                                stack.Add(trivia.GetStructure()!);
                            }
                        }
                    }
                }
            }
        }

        SyntaxToken ProcessNode(SemanticModel semanticModel, SyntaxNode node)
        {
            if (syntaxFacts.IsUsingAliasDirective(node))
            {
                syntaxFacts.GetPartsOfUsingAliasDirective(node, out _, out var aliasToken, out var name);
                if (!aliasToken.Span.IsEmpty)
                {
                    // Use 'name.Parent' because VB can't resolve the declared symbol directly from 'node'
                    var symbol = semanticModel.GetDeclaredSymbol(name.GetRequiredParent(), cancellationToken);
                    if (IsSymbolObsolete(symbol))
                        result.Add(aliasToken.Span);
                }

                return aliasToken;
            }
            else if (syntaxFacts.IsImplicitObjectCreationExpression(node))
            {
                syntaxFacts.GetPartsOfImplicitObjectCreationExpression(node, out var creationKeyword, out _, out _);
                if (!creationKeyword.Span.IsEmpty)
                {
                    var symbol = semanticModel.GetSymbolInfo(node, cancellationToken).Symbol;
                    if (IsSymbolObsolete(symbol) || IsSymbolObsolete(symbol?.ContainingType))
                        result.Add(creationKeyword.Span);
                }
            }

            return default;
        }

        void ProcessToken(SemanticModel semanticModel, SyntaxToken token)
        {
            if (syntaxFacts.IsIdentifier(token))
            {
                ProcessIdentifier(semanticModel, token);
            }
            else if (token.RawKind == _dimKeywordKind)
            {
                ProcessDimKeyword(result, semanticModel, token, cancellationToken);
            }
        }

        void ProcessIdentifier(SemanticModel semanticModel, SyntaxToken token)
        {
            if (syntaxFacts.IsDeclaration(token.Parent))
            {
                var symbol = semanticModel.GetDeclaredSymbol(token.Parent, cancellationToken);
                if (IsSymbolObsolete(symbol))
                    result.Add(token.Span);
            }
            else
            {
                var symbol = semanticModel.GetSymbolInfo(token, cancellationToken).Symbol;
                if (IsSymbolObsolete(symbol))
                    result.Add(token.Span);
            }
        }
    }

    protected static bool IsSymbolObsolete([NotNullWhen(true)] ISymbol? symbol)
    {
        // Avoid infinite recursion. Iteration limit chosen arbitrarily; cases are generally expected to complete on
        // the first iteration or fail completely.
        for (var i = 0; i < 5; i++)
        {
            if (symbol is IAliasSymbol alias)
            {
                symbol = alias.Target;
                continue;
            }

            if (symbol is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments: [var valueType] })
            {
                symbol = valueType;
                continue;
            }

            return symbol?.IsObsolete() ?? false;
        }

        // Unable to determine whether the symbol is considered obsolete
        return false;
    }
}
