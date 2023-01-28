// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CommonLanguageServerProtocol.Framework;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler.ServerLifetime;

[ExportLspServiceFactory(typeof(ILifeCycleManager), ProtocolConstants.RoslynLspLanguagesContract), Shared]
[ExportLspServiceFactory(typeof(ILifeCycleManager), ProtocolConstants.XamlLanguageContract)]
[ExportLspServiceFactory(typeof(ILifeCycleManager), ProtocolConstants.TypeScriptLanguageContract)]
internal class LspServiceLifeCycleManagerFactory : ILspServiceFactory
{
    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public LspServiceLifeCycleManagerFactory()
    {
    }

    public ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind)
    {
        var clientLanguageServerManagerService = lspServices.GetRequiredService<IClientLanguageServerManager>();
        return new LspServiceLifeCycleManager(clientLanguageServerManagerService);
    }
}
