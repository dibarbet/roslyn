// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.InlineHints;
using static Microsoft.CodeAnalysis.LanguageServer.Handler.InlayHint.InlayHintCache;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler.InlayHint;

[ExportCSharpVisualBasicLspService(typeof(InlayHintCache)), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class InlayHintCache() : ResolveCache<InlayHintCacheEntry>(maxCacheSize: 3)
{
    /// <summary>
    /// Cached data need to resolve a specific inlay hint item.
    /// </summary>
    internal record InlayHintCacheEntry(ImmutableArray<InlineHint> InlayHintMembers);
}
