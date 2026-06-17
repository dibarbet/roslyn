// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Composition;
using Microsoft.CodeAnalysis.Completion;
using static Microsoft.CodeAnalysis.LanguageServer.Handler.Completion.CompletionListCache;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler.Completion;

/// <summary>
/// Caches completion lists in between calls to CompletionHandler and
/// CompletionResolveHandler. Used to avoid unnecessary recomputation.
/// </summary>
[ExportCSharpVisualBasicLspService(typeof(CompletionListCache)), Shared(ProtocolConstants.LspServerInstanceSharingBoundary)]
internal sealed class CompletionListCache : ResolveCache<CacheEntry>
{
    // Note: this type is also constructed directly (e.g. by Razor) for its own caching, so the
    // constructor is a plain public default constructor that MEF uses as the importing constructor.
    public CompletionListCache() : base(maxCacheSize: 3)
    {
    }

    public sealed record CacheEntry(CompletionList CompletionList);
}
