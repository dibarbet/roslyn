// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO.Pipelines;
using Microsoft.ServiceHub.Framework;

namespace Microsoft.CodeAnalysis.LanguageServer.BrokeredServices;

/// <summary>
/// A non-MEF wrapper around <see cref="IServiceBroker"/> that allows callers to hold a stable broker instance
/// while the underlying broker becomes available.
/// </summary>
internal sealed class WrappedServiceBroker() : IServiceBroker
{
    private readonly TaskCompletionSource<IServiceBroker> _serviceBrokerTask = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void SetServiceBroker(IServiceBroker serviceBroker)
    {
        Contract.ThrowIfTrue(_serviceBrokerTask.Task.IsCompleted);
        serviceBroker.AvailabilityChanged += (s, e) => AvailabilityChanged?.Invoke(this, e);
        _serviceBrokerTask.SetResult(serviceBroker);
    }

    internal async Task<IServiceBroker> WaitForServiceBrokerAsync(CancellationToken cancellationToken)
    {
        _ = await _serviceBrokerTask.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return this;
    }

    public event EventHandler<BrokeredServicesChangedEventArgs>? AvailabilityChanged;

    public async ValueTask<IDuplexPipe?> GetPipeAsync(ServiceMoniker serviceMoniker, ServiceActivationOptions options = default, CancellationToken cancellationToken = default)
    {
        var serviceBroker = await _serviceBrokerTask.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await serviceBroker.GetPipeAsync(serviceMoniker, options, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<T?> GetProxyAsync<T>(ServiceRpcDescriptor serviceDescriptor, ServiceActivationOptions options = default, CancellationToken cancellationToken = default) where T : class
    {
        var serviceBroker = await _serviceBrokerTask.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable ISB001 // Dispose of proxies - caller is responsible for disposing the proxy.
        return await serviceBroker.GetProxyAsync<T>(serviceDescriptor, options, cancellationToken).ConfigureAwait(false);
#pragma warning restore ISB001 // Dispose of proxies
    }
}
