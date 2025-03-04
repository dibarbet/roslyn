// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// Defines an attribute to export an instance of <see cref="ILspService"/>.
/// This service instance lifetime is tied to that of the LSP server - a new instance is created
/// each time an LSP server is started, and disposed of when the server is shut down.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false), MetadataAttribute]
internal class ExportLspServiceAttribute : AbstractExportLspServiceAttribute
{
    public ExportLspServiceAttribute(
        Type serviceType, string contractName, WellKnownLspServerKinds serverKind = WellKnownLspServerKinds.Any)
        : base(serviceType, contractName, contractType: typeof(ILspService), fromFactory: false, serverKind)
    {
    }
}
