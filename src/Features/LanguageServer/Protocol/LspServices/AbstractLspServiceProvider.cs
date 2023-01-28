// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using Microsoft.CommonLanguageServerProtocol.Framework;

namespace Microsoft.CodeAnalysis.LanguageServer;

internal class AbstractLspServiceProvider
{
    private readonly IEnumerable<ExportFactory<ILspService, LspServiceMetadataView>> _lspServices;
    private readonly IEnumerable<ExportFactory<ILspServiceFactory, LspServiceMetadataView>> _lspServiceFactories;

    /// <summary>
    /// The MEF export contract name under which these services were imported.
    /// </summary>
    private readonly string _contractName;

    public AbstractLspServiceProvider(
        IEnumerable<ExportFactory<ILspService, LspServiceMetadataView>> lspServices,
        IEnumerable<ExportFactory<ILspServiceFactory, LspServiceMetadataView>> lspServiceFactories,
        string contractName)
    {
        _lspServices = lspServices;
        _lspServiceFactories = lspServiceFactories;
        _contractName = contractName;
    }

    public LspServices CreateServices(WellKnownLspServerKinds serverKind, ImmutableDictionary<Type, Lazy<object>> baseServices)
    {
        var lspServices = new LspServices(_lspServices, _lspServiceFactories, serverKind, _contractName, baseServices);
        return lspServices;
    }
}
