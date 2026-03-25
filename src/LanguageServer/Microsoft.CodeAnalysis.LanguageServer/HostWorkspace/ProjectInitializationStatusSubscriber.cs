// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.BrokeredServices;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.BrokeredServices;
using Microsoft.CodeAnalysis.LanguageServer.BrokeredServices.Services;
using Microsoft.CodeAnalysis.LanguageServer.BrokeredServices.Services.Definitions;
using Microsoft.CodeAnalysis.LanguageServer.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.ServiceHub.Framework;
using StreamJsonRpc;

namespace Microsoft.CodeAnalysis.LanguageServer.HostWorkspace;

internal sealed class ProjectInitializationStatusSubscriber : ILspService, IOnServiceBrokerInitialized, IDisposable
{
    private readonly ILspServiceBrokerFactory _serviceBrokerFactory;
    private readonly ILogger _logger;
    private readonly ProjectInitializationNotifier _projectInitializationNotifier;
    private readonly TaskCompletionSource _serviceAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ProjectInitializationCompleteObserver _projectInitializationCompleteObserver;
    private readonly CancellationTokenSource _disposalTokenSource = new();

    private IServiceBroker? _serviceBroker;
    private ServiceBrokerClient? _serviceBrokerClient;
    private IDisposable? _subscription;

    public ProjectInitializationStatusSubscriber(
        ILspServiceBrokerFactory serviceBrokerFactory,
        ProjectInitializationNotifier projectInitializationNotifier,
        ILoggerFactory loggerFactory)
    {
        _serviceBrokerFactory = serviceBrokerFactory;
        _logger = loggerFactory.CreateLogger<ProjectInitializationStatusSubscriber>();
        _projectInitializationNotifier = projectInitializationNotifier;
        _projectInitializationCompleteObserver = new ProjectInitializationCompleteObserver(_projectInitializationNotifier, _logger);
    }

    public async Task OnServiceBrokerInitializedAsync(ILspServiceBrokerFactory serviceBrokerFactory, CancellationToken cancellationToken)
    {
        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposalTokenSource.Token);
        var serviceBroker = serviceBrokerFactory.GetRequiredServiceBroker();

        _serviceBroker = serviceBroker;
        _serviceBroker.AvailabilityChanged += OnAvailabilityChanged;

        Contract.ThrowIfFalse(_serviceBrokerClient == null);
#pragma warning disable ISB001 // Dispose of proxies
        _serviceBrokerClient = new ServiceBrokerClient(serviceBroker, joinableTaskFactory: null);
#pragma warning restore ISB001 // Dispose of proxies

        var didSubscribe = await TrySubscribeAsync(linkedCancellationTokenSource.Token).ConfigureAwait(false);
        if (!didSubscribe)
        {
            _ = WaitForServiceAndSubscribeAsync(_disposalTokenSource.Token);
        }
    }

    private async Task WaitForServiceAndSubscribeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _serviceAvailable.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var didSubscribe = await TrySubscribeAsync(cancellationToken).ConfigureAwait(false);
            Contract.ThrowIfFalse(didSubscribe, $"Unable to subscribe to {Descriptors.RemoteProjectInitializationStatusService.Moniker}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> TrySubscribeAsync(CancellationToken cancellationToken)
    {
        Contract.ThrowIfNull(_serviceBrokerClient);

        using var rental = await _serviceBrokerClient.GetProxyAsync<IProjectInitializationStatusService>(
            Descriptors.RemoteProjectInitializationStatusService, cancellationToken).ConfigureAwait(false);

        if (rental.Proxy is not null)
        {
            _subscription = await rental.Proxy.SubscribeInitializationCompletionAsync(
                _projectInitializationCompleteObserver, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private void OnAvailabilityChanged(object? sender, BrokeredServicesChangedEventArgs e)
    {
        if (e.ImpactedServices.Contains(Descriptors.RemoteProjectInitializationStatusService.Moniker))
        {
            _serviceAvailable.TrySetResult();
        }
    }

    public void Dispose()
    {
        _disposalTokenSource.Cancel();
        _serviceBroker?.AvailabilityChanged -= OnAvailabilityChanged;
        _subscription?.Dispose();
        _serviceBrokerClient?.Dispose();
        _disposalTokenSource.Dispose();
    }

    internal sealed class ProjectInitializationCompleteObserver(
        ProjectInitializationNotifier projectInitializationNotifier,
        ILogger logger) : IObserver<ProjectInitializationCompletionState>
    {
        private readonly ProjectInitializationNotifier _projectInitializationNotifier = projectInitializationNotifier;
        private readonly ILogger _logger = logger;

        [JsonRpcMethod("onCompleted")]
        public void OnCompleted()
        {
        }

        [JsonRpcMethod("onError", UseSingleObjectParameterDeserialization = true)]
        public void OnError(Exception error)
        {
            _logger.LogError(error, "Devkit project initialization observer failed");
        }

        [JsonRpcMethod("onNext", UseSingleObjectParameterDeserialization = true)]
        public void OnNext(ProjectInitializationCompletionState value)
        {
            _logger.LogDebug("Devkit project initialization completed");
            VSCodeRequestTelemetryLogger.ReportProjectInitializationComplete();
            _ = _projectInitializationNotifier.SendProjectInitializationCompleteNotificationAsync(CancellationToken.None).AsTask().ReportNonFatalErrorAsync();
        }
    }
}
