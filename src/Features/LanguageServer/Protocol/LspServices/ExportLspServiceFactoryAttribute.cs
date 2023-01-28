// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.Linq;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// Exports an <see cref="ILspServiceFactory"/> that is used by LSP server instances
/// to create new instances of the <see cref="ILspService"/> each time an LSP server is started.
/// 
/// This is generally useful when the creation of an LSP service depends on other LSP services.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true), MetadataAttribute]
internal class ExportLspServiceFactoryAttribute : ExportAttribute
{
    /// <summary>
    /// The type of the service being exported.  Used during retrieval to find the matching service.
    /// </summary>
    public Type Type { get; }

    /// <summary>
    /// The LSP server for which this service applies to.  If null, this service applies to any server
    /// with the matching contract name.
    /// </summary>
    public WellKnownLspServerKinds ServerKind { get; }

    /// <summary>
    /// Indicates this service was provided by a factory.
    /// </summary>
    public bool FromFactory { get; } = true;

    public ExportLspServiceFactoryAttribute(Type type, string contractName, WellKnownLspServerKinds serverKind = WellKnownLspServerKinds.Any) : base(contractName, typeof(ILspServiceFactory))
    {
        Contract.ThrowIfFalse(type.GetInterfaces().Contains(typeof(ILspService)), $"{type.Name} does not inherit from {nameof(ILspService)}");
        Type = type;
        ServerKind = serverKind;
    }
}
