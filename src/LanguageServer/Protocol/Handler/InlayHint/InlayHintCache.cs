// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis.InlineHints;
using static Microsoft.CodeAnalysis.LanguageServer.Handler.InlayHint.InlayHintCache;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler.InlayHint;

[ExportCSharpVisualBasicLspService(typeof(InlayHintCache)), Shared(ProtocolConstants.LspServerInstanceSharingBoundary)]
internal sealed class InlayHintCache : ResolveCache<InlayHintCacheEntry>
{
    // Note: this type is also constructed directly (e.g. by Razor) for its own caching, so the
    // constructor is a plain public default constructor that MEF uses as the importing constructor.
    public InlayHintCache() : base(maxCacheSize: 3)
    {
    }

    /// <summary>
    /// Cached data need to resolve a specific inlay hint item.
    /// </summary>
    internal sealed record InlayHintCacheEntry(ImmutableArray<InlineHint> InlayHintMembers);
}
