// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;

namespace Microsoft.CodeAnalysis.LanguageServer;

internal class AbstractLspServiceProvider(
    IEnumerable<ExportFactory<ILspService, LspServiceMetadataView>> specificLspServices,
    IEnumerable<ExportFactory<ILspServiceFactory, LspServiceMetadataView>> specificLspServiceFactories)
{
    public LspServices CreateServices(WellKnownLspServerKinds serverKind, FrozenDictionary<string, ImmutableArray<BaseService>> baseServices)
    {
        var lspServices = new LspServices(specificLspServices, specificLspServiceFactories, serverKind, baseServices);

        return lspServices;
    }
}
