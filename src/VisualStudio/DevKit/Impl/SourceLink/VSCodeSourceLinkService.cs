// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.BrokeredServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.PdbSourceDocument;
using Microsoft.VisualStudio.Debugger.Contracts.SourceLink;
using Microsoft.VisualStudio.Debugger.Contracts.SymbolLocator;
using Microsoft.VisualStudio.LanguageServices.PdbSourceDocument;

namespace Microsoft.CodeAnalysis.LanguageServer.Services.SourceLink;

[Export(typeof(ISourceLinkService)), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class VSCodeSourceLinkService(IPdbSourceDocumentLogger logger) : AbstractSourceLinkService
{
    protected override async Task<SymbolLocatorResult?> LocateSymbolFileAsync(Workspace sourceWorkspace, SymbolLocatorPdbInfo pdbInfo, SymbolLocatorSearchFlags flags, CancellationToken cancellationToken)
    {
        var proxyFactory = sourceWorkspace.Services.GetService<IWorkspaceServiceBrokerProxy>();
        if (proxyFactory is null)
        {
            return null;
        }

        try
        {
            return await proxyFactory.UseProxyAsync<IDebuggerSymbolLocatorService, SymbolLocatorResult?>(
                BrokeredServiceDescriptors.DebuggerSymbolLocatorService,
                async (proxy, ct) => await proxy.LocateSymbolFileAsync(pdbInfo, flags, progress: null, ct).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (StreamJsonRpc.RemoteMethodNotFoundException)
        {
            // Older versions of DevKit use an invalid service descriptor - calling it will throw a RemoteMethodNotFoundException.
            // Just return null as there isn't a valid service available.
            return null;
        }
    }

    protected override async Task<SourceLinkResult?> GetSourceLinkAsync(Workspace sourceWorkspace, string url, string relativePath, CancellationToken cancellationToken)
    {
        var proxyFactory = sourceWorkspace.Services.GetService<IWorkspaceServiceBrokerProxy>();
        if (proxyFactory is null)
        {
            return null;
        }

        try
        {
            return await proxyFactory.UseProxyAsync<IDebuggerSourceLinkService, SourceLinkResult?>(
                BrokeredServiceDescriptors.DebuggerSourceLinkService,
                async (proxy, ct) => await proxy.GetSourceLinkAsync(url, relativePath, allowInteractiveLogin: false, ct).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (StreamJsonRpc.RemoteMethodNotFoundException)
        {
            // Older versions of DevKit use an invalid service descriptor - calling it will throw a RemoteMethodNotFoundException.
            // Just return null as there isn't a valid service available.
            return null;
        }
    }

    protected override IPdbSourceDocumentLogger? Logger => logger;
}
