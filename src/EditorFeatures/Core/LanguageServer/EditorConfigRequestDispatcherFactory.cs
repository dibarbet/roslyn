// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Composition;
using System.Text;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.LanguageServer.Handler.DocumentChanges;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer;

[Shared]
[Export(typeof(EditorConfigRequestDispatcherFactory))]
internal class EditorConfigRequestDispatcherFactory : AbstractRequestDispatcherFactory
{
    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public EditorConfigRequestDispatcherFactory([ImportMany(ProtocolConstants.EditorConfigLanguageContract)] IEnumerable<Lazy<IRequestHandlerProvider, RequestHandlerProviderMetadataView>> requestHandlerProviders) : base(requestHandlerProviders)
    {
    }
}

[ExportLspRequestHandlerProvider(ProtocolConstants.EditorConfigLanguageContract, typeof(EditorConfigDidChangeHandler)), Shared]
[Method(Methods.TextDocumentDidChangeName)]
internal class EditorConfigDidChangeHandler : DidChangeHandler
{
    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public EditorConfigDidChangeHandler()
    {
    }
}

[ExportLspRequestHandlerProvider(ProtocolConstants.EditorConfigLanguageContract, typeof(EditorConfigDidOpenHandler)), Shared]
[Method(Methods.TextDocumentDidOpenName)]
internal class EditorConfigDidOpenHandler : DidOpenHandler
{
    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public EditorConfigDidOpenHandler()
    {
    }
}

[ExportLspRequestHandlerProvider(ProtocolConstants.EditorConfigLanguageContract, typeof(EditorConfigDidCloseHandler)), Shared]
[Method(Methods.TextDocumentDidCloseName)]
internal class EditorConfigDidCloseHandler : DidCloseHandler
{
    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public EditorConfigDidCloseHandler()
    {
    }
}
