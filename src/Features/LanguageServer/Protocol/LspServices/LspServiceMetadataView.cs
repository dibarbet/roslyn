// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// Parses the MEF export metadata for <see cref="ExportLspServiceAttribute"/>
/// and <see cref="ExportLspServiceFactoryAttribute"/>.  Since these attributes allow
/// multiple exports per type, this takes care of aggregating the metadata for each attribute.
/// </summary>
internal class LspServiceMetadataView
{
    public ImmutableArray<LspServiceMetadata> ServiceMetadata;

    public LspServiceMetadataView(IDictionary<string, object> metadata)
    {
        // The format of the metadata dictionary depends on whether the service was exported with one attribute or multiple
        // If exported with one attribute, all the objects are single items.
        //
        // If exported with multiple attributes, each metadata item is an array.
        // The index in the array corresponds to which export attribute the metadata applies to.
        var contractMetadata = metadata[nameof(ExportLspServiceAttribute.ContractName)];
        var contractNames = contractMetadata is string[] contractArray ? contractArray.ToImmutableArray() : ImmutableArray.Create((string)contractMetadata);
        using var _ = ArrayBuilder<LspServiceMetadata>.GetInstance(out var metadataBuilder);
        if (contractNames.Length == 1)
        {
            var type = (Type)metadata[nameof(ExportLspServiceAttribute.Type)];
            var serverKind = (WellKnownLspServerKinds)metadata[nameof(ExportLspServiceAttribute.ServerKind)];
            var fromFactory = (bool)metadata[nameof(ExportLspServiceAttribute.FromFactory)];
            metadataBuilder.Add(new LspServiceMetadata(contractNames.Single(), type, serverKind, fromFactory));
        }
        else
        {
            
            var types = (Type[])metadata[nameof(ExportLspServiceAttribute.Type)];
            var serverKinds = (WellKnownLspServerKinds[])metadata[nameof(ExportLspServiceAttribute.ServerKind)];
            var fromFactory = (bool[])metadata[nameof(ExportLspServiceAttribute.FromFactory)];
            for (var i = 0; i < contractNames.Length; i++)
            {
                metadataBuilder.Add(new LspServiceMetadata(contractNames[i], types[i], serverKinds[i], fromFactory[i]));
            }
        }

        ServiceMetadata = metadataBuilder.ToImmutable();
    }

    internal record LspServiceMetadata(string ContractName, Type Type, WellKnownLspServerKinds ServerKind, bool FromFactory);
}
