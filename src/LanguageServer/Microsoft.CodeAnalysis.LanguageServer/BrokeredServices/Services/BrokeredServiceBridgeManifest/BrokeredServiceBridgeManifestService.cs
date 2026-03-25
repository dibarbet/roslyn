// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.ServiceHub.Framework;
using Microsoft.VisualStudio.Shell.ServiceBroker;
using Microsoft.VisualStudio.Utilities.ServiceBroker;

namespace Microsoft.CodeAnalysis.LanguageServer.BrokeredServices.Services.BrokeredServiceBridgeManifest;

internal sealed class BrokeredServiceBridgeManifest : IBrokeredServiceBridgeManifest, IExportedBrokeredService
{
    internal const string MonikerName = "Microsoft.VisualStudio.Server.IBrokeredServiceBridgeManifest";
    internal const string MonikerVersion = "0.1";
    private static readonly ServiceMoniker s_serviceMoniker = new(MonikerName, new Version(MonikerVersion));
    private static readonly ServiceRpcDescriptor s_serviceDescriptor = new ServiceJsonRpcDescriptor(
        s_serviceMoniker,
        ServiceJsonRpcDescriptor.Formatters.UTF8,
        ServiceJsonRpcDescriptor.MessageDelimiters.HttpLikeHeaders);

    internal static ServiceRpcDescriptor ServiceDescriptor => s_serviceDescriptor;

    private readonly ImmutableDictionary<ServiceMoniker, ServiceRegistration> _registeredServices;
    private readonly ILogger _logger;

    public BrokeredServiceBridgeManifest(ImmutableDictionary<ServiceMoniker, ServiceRegistration> registeredServices, ILoggerFactory loggerFactory)
    {
        _registeredServices = registeredServices;
        _logger = loggerFactory.CreateLogger<BrokeredServiceBridgeManifest>();
    }

    public ServiceRpcDescriptor Descriptor => ServiceDescriptor;

    public ValueTask<IReadOnlyCollection<ServiceMoniker>> GetAvailableServicesAsync(CancellationToken cancellationToken)
    {
        var services = (IReadOnlyCollection<ServiceMoniker>)[.. _registeredServices
            .Select(s => s.Key)
            .Where(s => s.Name.StartsWith("Microsoft.CodeAnalysis.LanguageServer.", StringComparison.Ordinal) ||
                        s.Name.StartsWith("Microsoft.VisualStudio.LanguageServer.", StringComparison.Ordinal) ||
                        s.Name.StartsWith("Microsoft.VisualStudio.LanguageServices.", StringComparison.Ordinal))];
        _logger.LogDebug($"Proffered services: {string.Join(',', services.Select(s => s.ToString()))}");
        return new(services);
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
