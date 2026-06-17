// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// Exports an instance of <see cref="ILspService"/>.
/// <para>
/// Pair this with <c>[Shared(ProtocolConstants.LspServerInstanceSharingBoundary)]</c> to get a fresh
/// instance per LSP server that is shared amongst that server's other services (replacing the old
/// <see cref="ILspServiceFactory"/> pattern). A service that needs other per-server services or the
/// per-server context imports <see cref="LspServices"/> and resolves them via
/// <see cref="LspServices.GetRequiredService{T}"/>.
/// </para>
/// <para>
/// Pair instead with <c>[Shared]</c> (no boundary) for a service that is genuinely shared across all
/// server instances in the same MEF container (equivalent to <see cref="ExportStatelessLspServiceAttribute"/>);
/// such a service must not import <see cref="LspServices"/>.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false), MetadataAttribute]
internal class ExportLspServiceAttribute(
    Type serviceType, string contractName, WellKnownLspServerKinds serverKind = WellKnownLspServerKinds.Any)
    : AbstractExportLspServiceAttribute(
        serviceType, contractName, contractType: typeof(ILspService), isStateless: false, serverKind);
