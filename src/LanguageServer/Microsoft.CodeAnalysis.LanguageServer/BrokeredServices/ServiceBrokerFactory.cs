// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.BrokeredServices;
using Microsoft.CodeAnalysis.EditAndContinue;
using Microsoft.CodeAnalysis.LanguageServer.BrokeredServices.Services.BrokeredServiceBridgeManifest;
using Microsoft.CodeAnalysis.LanguageServer.HostWorkspace;
using Microsoft.CodeAnalysis.Remote.ProjectSystem;
using Microsoft.Extensions.Logging;
using Microsoft.ServiceHub.Framework;
using Microsoft.VisualStudio.Shell.ServiceBroker;
using Microsoft.VisualStudio.Utilities.ServiceBroker;
using ExportProvider = Microsoft.VisualStudio.Composition.ExportProvider;

namespace Microsoft.CodeAnalysis.LanguageServer.BrokeredServices;

internal sealed class ServiceBrokerFactory : ILspServiceBrokerFactory, IDisposable
{
    private readonly LspServices _lspServices;
    private readonly ExportProvider _exportProvider;
    private readonly BrokeredServiceBridgeProvider _bridgeProvider;
    private readonly LanguageServerWorkspaceFactory _workspaceFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly WrappedServiceBroker _wrappedServiceBroker = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private BrokeredServiceContainer? _container;

    public ServiceBrokerFactory(
        LspServices lspServices,
        ExportProvider exportProvider,
        BrokeredServiceBridgeProvider bridgeProvider,
        LanguageServerWorkspaceFactory workspaceFactory,
        ILoggerFactory loggerFactory)
    {
        _lspServices = lspServices;
        _exportProvider = exportProvider;
        _bridgeProvider = bridgeProvider;
        _workspaceFactory = workspaceFactory;
        _loggerFactory = loggerFactory;
    }

    public IServiceBroker? TryGetFullAccessServiceBroker() => _container is null ? null : _wrappedServiceBroker;

    public IServiceBroker GetRequiredServiceBroker() => _wrappedServiceBroker;

    public async Task CreateAsync()
    {
        if (_container is not null)
        {
            throw new InvalidOperationException("Brokered service container has already been created.");
        }

        var container = await BrokeredServiceContainer.CreateAsync(_exportProvider, _cancellationTokenSource.Token).ConfigureAwait(false);
        RegisterManualServices(container);
        ProfferManualServices(container);

        var serviceBroker = container.GetFullAccessServiceBroker();
        _wrappedServiceBroker.SetServiceBroker(serviceBroker);
        SetWorkspaceServiceBroker(_workspaceFactory.HostWorkspace, _wrappedServiceBroker);

        _container = container;
    }

    public async Task CreateAndConnectAsync(string brokeredServicePipeName)
    {
        await CreateAsync().ConfigureAwait(false);

        await _bridgeProvider.SetupBrokeredServicesBridgeAsync(
                brokeredServicePipeName, _container!, _cancellationTokenSource.Token);

        var onInitializeList = _lspServices.GetRequiredServices<IOnServiceBrokerInitialized>();

        foreach (var onInitialize in onInitializeList)
        {
            await onInitialize.OnServiceBrokerInitializedAsync(this, _cancellationTokenSource.Token).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        SetWorkspaceServiceBroker(_workspaceFactory.HostWorkspace, serviceBroker: null);
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
    }

    private void ProfferManualServices(BrokeredServiceContainer container)
    {
        _ = Proffer(
            container,
            new WorkspaceProjectFactoryService(
            _workspaceFactory,
            _lspServices.GetRequiredService<ProjectInitializationStatusSubscriber>(),
            _loggerFactory));
        _ = Proffer(container, new BrokeredServiceBridgeManifest(container.GetRegisteredServices(), _loggerFactory));
    }

    private static void RegisterManualServices(BrokeredServiceContainer container)
    {
        container.RegisterServices(new Dictionary<ServiceMoniker, ServiceRegistration>
        {
            { WorkspaceProjectFactoryServiceDescriptor.ServiceDescriptor.Moniker, new ServiceRegistration(ServiceAudience.Local, null, allowGuestClients: false) },
            { BrokeredServiceBridgeManifest.ServiceDescriptor.Moniker, new ServiceRegistration(ServiceAudience.Local, null, allowGuestClients: false) },
        });
    }

    private static IDisposable Proffer(BrokeredServiceContainer container, IExportedBrokeredService service)
    {
        var descriptor = service.Descriptor;
        Contract.ThrowIfNull(descriptor);

        return container.Proffer(
            descriptor,
            async (_, _, _, cancellationToken) =>
            {
                await service.InitializeAsync(cancellationToken).ConfigureAwait(false);
                return service;
            });
    }

    private static void SetWorkspaceServiceBroker(Workspace workspace, IServiceBroker? serviceBroker)
    {
        if (workspace.Services.GetService<IWorkspaceServiceBrokerProxy>() is WorkspaceServiceBrokerProxy proxyFactory)
        {
            proxyFactory.SetServiceBroker(serviceBroker);
        }
    }
}
