// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// Defines a factory that should create a new instance of an <see cref="ILspService"/>.
/// This is typically used when the <see cref="ILspService"/> depends on other <see cref="ILspService"/>
/// in its construction.
/// </summary>
internal interface ILspServiceFactory
{
    ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind);
}
