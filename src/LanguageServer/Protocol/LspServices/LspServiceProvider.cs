// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis.Host.Mef;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// Creates the per-server <see cref="LspServices"/> for an LSP server by opening a fresh
/// <see cref="ProtocolConstants.LspServerInstanceSharingBoundary"/> sharing-boundary scope via
/// <see cref="ExportFactory{T}.CreateExport"/>.
/// <para>
/// A single global instance of this provider serves every server kind and contract; the server's
/// contract is selected at runtime from the seeded <see cref="WellKnownLspServerKinds"/> inside
/// <see cref="LspServices.Initialize"/>. Each <see cref="CreateServices"/> call opens an isolated scope,
/// so concurrent servers (even ones sharing a contract) never see each other's per-server services.
/// </para>
/// </summary>
[Export(typeof(LspServiceProvider)), Shared]
internal sealed class LspServiceProvider
{
    private readonly ExportFactory<LspServices> _exportFactory;

    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public LspServiceProvider(
        [SharingBoundary(ProtocolConstants.LspServerInstanceSharingBoundary)] ExportFactory<LspServices> exportFactory)
    {
        _exportFactory = exportFactory;
    }

    public LspServices CreateServices(WellKnownLspServerKinds serverKind, FrozenDictionary<string, ImmutableArray<BaseService>> baseServices)
    {
        // Open a new per-server sharing-boundary scope. The returned export's lifetime owns every scoped
        // service; disposing the LspServices on server shutdown disposes this scope.
        var export = _exportFactory.CreateExport();
        var lspServices = export.Value;
        lspServices.Initialize(serverKind, baseServices, export);
        return lspServices;
    }
}
