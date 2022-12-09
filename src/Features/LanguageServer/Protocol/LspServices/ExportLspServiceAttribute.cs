// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.Linq;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// Defines an attribute to export an instance of <see cref="ILspService"/>.
/// <see cref="ILspService"/> instances are re-created each time a server starts and disposed of when it shuts down.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false), MetadataAttribute]
internal class ExportLspServiceAttribute : ExportAttribute
{
    /// <summary>
    /// The type of the service being exported.  Used during retrieval to find the matching service.
    /// </summary>
    public Type Type { get; }

    /// <summary>
    /// The LSP server for which this service applies to.  If null, this service applies to any server
    /// with the matching contract name.
    /// </summary>
    public WellKnownLspServerKinds? ServerKind { get; }

    /// <summary>
    /// Indicates this service was provided was not provided by a factory.
    /// </summary>
    public bool FromFactory = false;

    public ExportLspServiceAttribute(Type type, string contractName, WellKnownLspServerKinds serverKind = WellKnownLspServerKinds.Any) : base(contractName, typeof(ILspService))
    {
        Contract.ThrowIfFalse(type.GetInterfaces().Contains(typeof(ILspService)), $"{type.Name} does not inherit from {nameof(ILspService)}");
        Type = type;
        ServerKind = serverKind;
    }
}
