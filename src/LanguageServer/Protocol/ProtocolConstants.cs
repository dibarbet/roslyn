// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;

namespace Microsoft.CodeAnalysis.LanguageServer;

internal sealed class ProtocolConstants
{
    public static ImmutableArray<string> RoslynLspLanguages = [LanguageNames.CSharp, LanguageNames.VisualBasic, LanguageNames.FSharp];

    public const string RoslynLspLanguagesContract = "RoslynLspLanguages";

    public const string TypeScriptLanguageContract = "TypeScriptLspLanguage";

    /// <summary>
    /// The name of the MEF sharing boundary that defines the lifetime of a single LSP server instance.
    /// <para>
    /// Services that need a fresh instance per LSP server (and shared amongst that server's other
    /// services) are exported with <c>[Shared(ProtocolConstants.LspServerInstanceSharingBoundary)]</c>.
    /// <see cref="LspServices"/> is the root of this boundary; one isolated set of scoped instances is
    /// created per <see cref="System.Composition.ExportFactory{T}.CreateExport"/> call (i.e. per server).
    /// Globally <c>[Shared]</c> services remain shared across all servers in the same MEF container.
    /// </para>
    /// </summary>
    public const string LspServerInstanceSharingBoundary = "LspServerInstance";
}
