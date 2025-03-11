// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Host.Mef;
using static Microsoft.CodeAnalysis.LanguageServer.Handler.Completion.CompletionListCache;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler.Completion
{
    /// <summary>
    /// Caches completion lists in between calls to CompletionHandler and
    /// CompletionResolveHandler. Used to avoid unnecessary recomputation.
    /// </summary>
    [ExportCSharpVisualBasicLspService(typeof(CompletionListCache)), Shared]
    [method: ImportingConstructor]
    [method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    internal class CompletionListCache() : ResolveCache<CacheEntry>(maxCacheSize: 3)
    {
        public record CacheEntry(CompletionList CompletionList);
    }
}
