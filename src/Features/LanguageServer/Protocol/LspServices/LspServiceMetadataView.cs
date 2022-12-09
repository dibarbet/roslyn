// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;

namespace Microsoft.CodeAnalysis.LanguageServer;

internal class LspServiceMetadataView
{
    public Type Type { get; set; }

    public WellKnownLspServerKinds ServerKind { get; set; }

    public bool FromFactory { get; set; }

    public LspServiceMetadataView(IDictionary<string, object> metadata)
    {
        var handlerMetadata = (Type)metadata[nameof(Type)];
        Type = handlerMetadata;

        ServerKind = (WellKnownLspServerKinds)metadata[nameof(ServerKind)];
        FromFactory = (bool)metadata[nameof(FromFactory)];
    }

    public LspServiceMetadataView(Type type)
    {
        Type = type;
        ServerKind = WellKnownLspServerKinds.Any;
        FromFactory = false;
    }
}
