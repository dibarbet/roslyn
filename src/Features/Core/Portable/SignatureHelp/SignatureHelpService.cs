// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.SignatureHelp;

internal abstract class SignatureHelpService : ILanguageService
{
    private readonly LanguageServices _services;
    private ImmutableArray<ISignatureHelpProvider> _providers;

    protected SignatureHelpService(LanguageServices services)
    {
        _services = services;
    }

    private ImmutableArray<ISignatureHelpProvider> GetProviders()
    {
        if (_providers.IsDefault)
        {
            var mefExporter = _services.SolutionServices.ExportProvider;

            var providers = ExtensionOrderer
                .Order(mefExporter.GetExports<ISignatureHelpProvider, OrderableLanguageMetadata>()
                    .Where(lz => lz.Metadata.Language == _services.Language))
                .Select(lz => lz.Value)
                .ToImmutableArray();

            ImmutableInterlocked.InterlockedCompareExchange(ref _providers, providers, default);
        }

        return _providers;
    }

    public async Task<(ISignatureHelpProvider provider, SignatureHelpItems items)> GetSignatureHelpItemsAsync(
        int caretPosition,
        SignatureHelpTriggerInfo triggerInfo,
        SignatureHelpOptions options,
        Document document,
        CancellationToken cancellationToken)
    {
        ISignatureHelpProvider bestProvider = null;
        SignatureHelpItems bestItems = null;

        var providers = GetProviders();

        // TODO(cyrusn): We're calling into extensions, we need to make ourselves resilient
        // to the extension crashing.
        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentItems = await provider.GetItemsAsync(document, caretPosition, triggerInfo, options, cancellationToken).ConfigureAwait(false);
            if (currentItems != null && currentItems.ApplicableSpan.IntersectsWith(caretPosition))
            {
                // If another provider provides sig help items, then only take them if they
                // start after the last batch of items.  i.e. we want the set of items that
                // conceptually are closer to where the caret position is.  This way if you have:
                //
                //  Goo(new Bar($$
                //
                // Then invoking sig help will only show the items for "new Bar(" and not also
                // the items for "Goo(..."
                if (IsBetter(bestItems, currentItems.ApplicableSpan))
                {
                    bestItems = currentItems;
                    bestProvider = provider;
                }
            }
        }

        return (bestProvider, bestItems);

        static bool IsBetter(SignatureHelpItems bestItems, TextSpan? currentTextSpan)
        {
            // If we have no best text span, then this span is definitely better.
            if (bestItems == null)
            {
                return true;
            }

            // Otherwise we want the one that is conceptually the innermost signature.  So it's
            // only better if the distance from it to the caret position is less than the best
            // one so far.
            return currentTextSpan.Value.Start > bestItems.ApplicableSpan.Start;
        }
    }
}
