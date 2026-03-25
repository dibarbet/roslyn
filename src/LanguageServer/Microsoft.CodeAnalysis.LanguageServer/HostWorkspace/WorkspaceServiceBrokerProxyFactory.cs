// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Composition;
using Microsoft.CodeAnalysis.BrokeredServices;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.ServiceHub.Framework;

namespace Microsoft.CodeAnalysis.LanguageServer.HostWorkspace;

[ExportWorkspaceServiceFactory(typeof(IWorkspaceServiceBrokerProxy), [WorkspaceKind.Host]), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class WorkspaceServiceBrokerProxyFactory() : IWorkspaceServiceFactory
{
    [Obsolete(MefConstruction.FactoryMethodMessage, error: true)]
    public IWorkspaceService CreateService(HostWorkspaceServices workspaceServices)
        => new WorkspaceServiceBrokerProxy();
}

internal sealed class WorkspaceServiceBrokerProxy : IWorkspaceServiceBrokerProxy
{
    private readonly Lock _lock = new();

    private IServiceBroker? _serviceBroker;

    internal void SetServiceBroker(IServiceBroker? serviceBroker)
    {
        lock (_lock)
        {
            _serviceBroker = serviceBroker;
        }
    }

    public async ValueTask<TResult?> UseProxyAsync<TProxy, TResult>(ServiceRpcDescriptor descriptor, Func<TProxy, CancellationToken, ValueTask<TResult?>> action, CancellationToken cancellationToken) where TProxy : class
    {
        IServiceBroker? serviceBroker;
        lock (_lock)
        {
            serviceBroker = _serviceBroker;
        }

        if (serviceBroker is null)
            return default(TResult);

        var proxy = await serviceBroker.GetProxyAsync<TProxy>(descriptor, cancellationToken: cancellationToken).ConfigureAwait(false);
        using ((IDisposable?)proxy)
        {
            if (proxy is null)
                return default(TResult);

            return await action(proxy, cancellationToken).ConfigureAwait(false);
        }
    }
}
