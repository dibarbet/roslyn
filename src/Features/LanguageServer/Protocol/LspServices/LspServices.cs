// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CommonLanguageServerProtocol.Framework;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// <para>
/// This type holds instances of <see cref="ILspService"/> for each <see cref="AbstractLanguageServer{TRequestContext}"/>.
/// </para>
/// 
/// <para>
/// There is a single instance of the service associated with each server instance.
/// Each time a new server is started (or a server restarted), new instances are lazily created.
/// When a server shuts down, all LSP service instances associated with it are disposed of.
/// </para>
/// 
/// <para>
/// LSP services are provided by the <see cref="ExportLspServiceAttribute"/> and <see cref="ExportLspServiceFactoryAttribute"/>
/// A service is exported with a particular contract, <see cref="ExportAttribute.ContractName"/>.  This contract
/// allows a server to only import (and therefore load dlls from) services that match the LSP server being started.
/// For example we want to avoid loading XAML dlls when starting a C# server.  Services can also be exported with
/// a <see cref="WellKnownLspServerKinds"/> flag indicating a more granular
/// export is required (e.g. when a service only applies to the Razor c# server).
/// </para>
/// 
/// <para>
/// This also supports multiple export attributes per <see cref="ILspService"/> type.  
/// When an lsp service applies to multiple languages it is useful to be able to export it multiple times
/// for each contract that it applies to.  Each server will get a new instance (via the export factory), but we
/// can avoid creating multiple type definitions for it.
/// </para>
/// 
/// </summary>
/// <remarks>
/// The goal of having a single instance of an <see cref="ILspService"/> per server is accomplished
/// using a <see cref="ExportFactory{T, TMetadata}"/>.  A type MEF imported as an ExportFactory is
/// instantiated (and dependencies resolved) on every call to <see cref="ExportFactory{T}.CreateExport"/>.
/// 
/// So for each server we import all the ILspServices as an ExportFactory and call CreateExport on them
/// once for the lifetime of the server.
/// </remarks>
internal class LspServices : ILspServices
{
    private readonly IEnumerable<Lazy<Export<ILspService>, LspServiceMetadataView>> _serviceExport;
    private readonly IEnumerable<Lazy<Export<ILspServiceFactory>, LspServiceMetadataView>> _factoryExport;

    private readonly ImmutableDictionary<Type, Lazy<ILspService, LspServiceMetadataView.LspServiceMetadata>> _lazyMefLspServices;

    /// <summary>
    /// A set of base services that must be created on initialization because they
    /// depend on base components of the language server (e.g. the streamjsonrpc instance).
    /// </summary>
    private readonly ImmutableDictionary<Type, Lazy<object>> _baseServices;

    /// <summary>
    /// Gates access to <see cref="servicesFromFactoriesToDispose"/>.
    /// </summary>
    private readonly object _gate = new();
    private readonly HashSet<IDisposable> servicesFromFactoriesToDispose = new(ReferenceEqualityComparer.Instance);

    public LspServices(
        IEnumerable<ExportFactory<ILspService, LspServiceMetadataView>> mefLspServices,
        IEnumerable<ExportFactory<ILspServiceFactory, LspServiceMetadataView>> mefLspServiceFactories,
        WellKnownLspServerKinds serverKind,
        string contractName,
        ImmutableDictionary<Type, Lazy<object>> baseServices)
    {
        // TODO - need to be non-shared?

        // Create lazies representing the Export instances of the lsp instances from the ExportFactory.
        // These are saved so that we can dispose of the Export instances at the end.
        _serviceExport = mefLspServices.Select(exportFactory => new Lazy<Export<ILspService>, LspServiceMetadataView>(() => exportFactory.CreateExport(), exportFactory.Metadata));
        _factoryExport = mefLspServiceFactories.Select(e => new Lazy<Export<ILspServiceFactory>, LspServiceMetadataView>(() => e.CreateExport(), e.Metadata));

        // Convert MEF ExportFactory<ILspServiceFactory> instances to the lazy ILspService objects that they create.
        var servicesFromFactories = _factoryExport.Select(lazyFactoryExport
            => new Lazy<ILspService, LspServiceMetadataView>(() => lazyFactoryExport.Value.Value.CreateILspService(this, serverKind), lazyFactoryExport.Metadata));

        // Convert MEF ExportFactory<ILspService> to lazy ILspService objects.
        var services = _serviceExport.Select(lazyServiceExport => new Lazy<ILspService, LspServiceMetadataView>(() => lazyServiceExport.Value.Value, lazyServiceExport.Metadata));
        
        // Combine the services and services from factories
        services = services.Concat(servicesFromFactories);

        // Next we create a dictionary of the exported type to the actual instance.
        // A single instance can be exported multiple times for different types, however
        // a single type must map to only 1 service instance.
        var typesToService = new Dictionary<Type, Lazy<ILspService, LspServiceMetadataView.LspServiceMetadata>>();
        foreach (var lazyService in services)
        {
            foreach (var serviceMetadata in lazyService.Metadata.ServiceMetadata)
            {
                // Make sure that we only include services exported for the specified contract and server kind.
                if (ShouldInclude(serviceMetadata, contractName, serverKind))
                {
                    // A single instance may be exported as multiple types, but we should never get multiple instances for a single type.
                    typesToService.Add(serviceMetadata.Type, new Lazy<ILspService, LspServiceMetadataView.LspServiceMetadata>(() => lazyService.Value, serviceMetadata));
                }
            }
        }

        _lazyMefLspServices = typesToService.ToImmutableDictionary();

        // Base services don't necessarily inherit from ILSPService (e.g. Clasp services), so these are objects.
        _baseServices = baseServices;

        static bool ShouldInclude(LspServiceMetadataView.LspServiceMetadata metadata, string contractName, WellKnownLspServerKinds serverKind)
        {
            return metadata.ContractName == contractName && (metadata.ServerKind == serverKind || metadata.ServerKind == WellKnownLspServerKinds.Any);
        }
    }

    public T GetRequiredService<T>() where T : notnull
    {
        T? service;

        // Check the base services first
        service = GetBaseService<T>() ?? GetService<T>();

        Contract.ThrowIfNull(service, $"Missing required LSP service {typeof(T).FullName}");
        return service;
    }

    public T? GetService<T>()
    {
        var type = typeof(T);
        var service = (T?)TryGetService(type);

        return service;
    }

    public IEnumerable<T> GetRequiredServices<T>()
    {
        var baseService = GetBaseService<T>();
        var mefServices = GetMefServices<T>();

        return baseService != null ? mefServices.Concat(baseService) : mefServices;
    }

    public object? TryGetService(Type type)
    {
        object? lspService;
        if (_lazyMefLspServices.TryGetValue(type, out var lazyService))
        {
            // If we are creating a LSP service from a factory for the first time, we need to check
            // if it is disposable after creation and keep it around to dispose of on shutdown.
            // Non-factory services and the factory instance itself will be disposed of when we dispose of the Export.
            var checkDisposal = lazyService.Metadata.FromFactory && !lazyService.IsValueCreated;

            lspService = lazyService.Value;
            if (checkDisposal && lspService is IDisposable disposable)
            {
                lock (_gate)
                {
                    var res = servicesFromFactoriesToDispose.Add(disposable);
                }
            }

            lspService = lazyService.Value;
            return lspService;
        }

        lspService = null;
        return lspService;
    }

    public ImmutableArray<Type> GetRegisteredServices() => _lazyMefLspServices.Keys.ToImmutableArray();

    public bool SupportsGetRegisteredServices()
    {
        return true;
    }

    private T? GetBaseService<T>()
    {
        if (_baseServices.TryGetValue(typeof(T), out var baseService))
        {
            return (T)baseService.Value;
        }

        return default;
    }

    private IEnumerable<T> GetMefServices<T>()
    {
        if (typeof(T) == typeof(IMethodHandler))
        {
            // HACK: There is special handling for the IMethodHandler to make sure that its types remain lazy
            // Special case this to avoid providing them twice.
            yield break;
        }

        var allServices = GetRegisteredServices();
        foreach (var service in allServices)
        {
            var @interface = service.GetInterface(typeof(T).Name);
            if (@interface is not null)
            {
                var instance = TryGetService(service);
                if (instance is not null)
                {
                    yield return (T)instance;
                }
                else
                {
                    throw new Exception("Service failed to construct");
                }
            }
        }
    }

    public void Dispose()
    {
        ImmutableArray<IDisposable> disposableServices;
        lock (_gate)
        {
            disposableServices = servicesFromFactoriesToDispose.ToImmutableArray();
            servicesFromFactoriesToDispose.Clear();
        }

        // First dispose of any disposable services created by the factories.
        foreach (var disposableService in disposableServices)
        {
            try
            {
                disposableService.Dispose();
            }
            catch (Exception ex) when (FatalError.ReportAndCatch(ex))
            {
            }
        }

        // Second, dispose of the Export instances themselves so MEF knows it can also release their dependencies.
        foreach (var lazyExport in _serviceExport)
        {
            if (lazyExport.IsValueCreated)
            {
                lazyExport.Value.Dispose();
            }
        }

        foreach (var lazyFactoryExport in _factoryExport)
        {
            if (lazyFactoryExport.IsValueCreated)
            {
                lazyFactoryExport.Value.Dispose();
            }
        }

        // Third, dispose of all the base services.
        foreach (var baseService in _baseServices.Values)
        {
            if (baseService.IsValueCreated && baseService.Value is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
