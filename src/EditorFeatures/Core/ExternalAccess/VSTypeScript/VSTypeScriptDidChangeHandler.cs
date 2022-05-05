// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.LanguageServer.Handler.DocumentChanges;

namespace Microsoft.CodeAnalysis.ExternalAccess.VSTypeScript;

#pragma warning disable RS0023 // Parts exported with MEFv2 must be marked with 'SharedAttribute'
[ExportLspService(typeof(DidChangeHandler), ProtocolConstants.TypeScriptLanguageContract)]
#pragma warning restore RS0023 // Parts exported with MEFv2 must be marked with 'SharedAttribute'
internal class VSTypeScriptDidChangeHandler : DidChangeHandler
{
    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public VSTypeScriptDidChangeHandler()
    {
    }
}
