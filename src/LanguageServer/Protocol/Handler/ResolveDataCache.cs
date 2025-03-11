﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using Microsoft.CodeAnalysis.Host.Mef;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler;

/// <summary>
/// Caches arbitrary data object in between calls to Handler and ResolveHandler.
/// Used to minimize passing around request resolve data.
/// </summary>
[ExportCSharpVisualBasicLspService(typeof(ResolveDataCache)), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class ResolveDataCache() : ResolveCache<object>(maxCacheSize: 3)
{
}
