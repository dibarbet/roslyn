// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// Obsolete. Export the service directly with <c>[ExportCSharpVisualBasicLspService]</c> /
/// <c>[ExportLspService]</c> + <c>[Shared(ProtocolConstants.LspServerInstanceSharingBoundary)]</c> and
/// import <see cref="LspServices"/> in its importing constructor when it needs other per-server services
/// or the server kind. This replaces the per-server "factory" mechanism with a MEF sharing boundary.
/// </summary>
[Obsolete("Export the service directly with [ExportCSharpVisualBasicLspService]/[ExportLspService] + [Shared(ProtocolConstants.LspServerInstanceSharingBoundary)], importing LspServices for per-server context, instead of implementing ILspServiceFactory.")]
internal interface ILspServiceFactory
{
    /// <summary>
    /// Some LSP services need to know the client capabilities on construction or
    /// need to know about other <see cref="ILspService"/> instances to be constructed.
    /// </summary>
    ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind);
}
