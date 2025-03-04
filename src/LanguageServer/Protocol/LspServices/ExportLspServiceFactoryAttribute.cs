// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// Exports an <see cref="ILspServiceFactory"/> that is used by LSP server instances
/// that require access to other LSP services on creation.
/// The services created by the <see cref="ILspServiceFactory"/> are disposed of by <see cref="LspServices"/>
/// when the LSP server instance shuts down.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false), MetadataAttribute]
internal class ExportLspServiceFactoryAttribute : AbstractExportLspServiceAttribute
{
    public ExportLspServiceFactoryAttribute(
        Type serviceType, string contractName, WellKnownLspServerKinds serverKind = WellKnownLspServerKinds.Any)
        : base(serviceType, contractName, contractType: typeof(ILspServiceFactory), fromFactory: true, serverKind)
    {
    }
}
