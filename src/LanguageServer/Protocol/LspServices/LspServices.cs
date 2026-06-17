// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CommonLanguageServerProtocol.Framework;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// Holds the set of services for a single LSP server instance.
/// <para>
/// This is the root of the <see cref="ProtocolConstants.LspServerInstanceSharingBoundary"/> MEF sharing
/// boundary: it is created by <see cref="LspServiceProvider"/> via
/// <see cref="ExportFactory{T}.CreateExport"/> once per server, and <see cref="Initialize"/> seeds the
/// per-server runtime context (server kind, base services, and the scope's lifetime). Services that are
/// exported <c>[Shared(ProtocolConstants.LspServerInstanceSharingBoundary)]</c> get one instance per
/// server scope; globally <c>[Shared]</c> services are shared across all servers in the container.
/// </para>
/// <para>
/// The obsolete <see cref="ILspServiceFactory"/> mechanism is still imported and invoked here for
/// back-compat while external-access partners (CompilerDeveloperSdk, XAML) migrate to the non-factory
/// export attributes; the instances those factories produce are tracked for disposal by this type (they
/// are not MEF-managed parts). All in-repo Roslyn/TypeScript/Razor services use the <c>[Shared(boundary)]</c>
/// export model above.
/// </para>
/// </summary>
[Export, Shared(ProtocolConstants.LspServerInstanceSharingBoundary)]
internal sealed class LspServices : ILspServices, IMethodHandlerProvider
{
    private readonly ImmutableArray<Lazy<ILspService, LspServiceMetadataView>> _roslynLspServices;
    private readonly ImmutableArray<Lazy<ILspService, LspServiceMetadataView>> _typeScriptLspServices;
#pragma warning disable CS0618 // ILspServiceFactory is obsolete; retained only to invoke external-access factory implementations.
    private readonly ImmutableArray<Lazy<ILspServiceFactory, LspServiceMetadataView>> _roslynLspServiceFactories;
    private readonly ImmutableArray<Lazy<ILspServiceFactory, LspServiceMetadataView>> _typeScriptLspServiceFactories;
#pragma warning restore CS0618

    /// <summary>
    /// The services selected for this server's contract and server kind. Built in <see cref="Initialize"/>
    /// once the server kind is known.
    /// </summary>
    private FrozenDictionary<string, Lazy<ILspService, LspServiceMetadataView>>? _lazyMefLspServices;

    /// <summary>
    /// The set of service type names that are produced by an <see cref="ILspServiceFactory"/> rather than
    /// being MEF-managed parts. These instances are disposed manually by this type (see <see cref="Dispose"/>);
    /// MEF-managed parts are disposed by the sharing-boundary scope (scoped services) or the container
    /// (globally shared services) instead.
    /// </summary>
    private FrozenSet<string> _factoryProducedServiceNames = FrozenSet<string>.Empty;

    /// <summary>
    /// A set of base services that apply to all Roslyn lsp services.
    /// Unfortunately MEF doesn't provide a good way to export something for multiple contracts with metadata
    /// so these are manually created in <see cref="RoslynLanguageServer"/> and seeded via <see cref="Initialize"/>.
    /// </summary>
    private FrozenDictionary<string, ImmutableArray<BaseService>> _baseServices = FrozenDictionary<string, ImmutableArray<BaseService>>.Empty;

    /// <summary>
    /// The lifetime of this server's sharing-boundary scope. Disposing it tears down all scoped services
    /// (including this instance). Set in <see cref="Initialize"/>.
    /// </summary>
    private IDisposable? _scopeLifetime;

    /// <summary>
    /// The kind of LSP server this instance belongs to. Seeded in <see cref="Initialize"/>; lets per-server
    /// services read the server kind (e.g. for telemetry) without being passed it by a factory.
    /// </summary>
    public WellKnownLspServerKinds ServerKind { get; private set; }

    /// <summary>
    /// Gates access to <see cref="_servicesToDispose"/> and <see cref="_disposed"/>.
    /// </summary>
    private readonly object _gate = new();
    private readonly HashSet<IDisposable> _servicesToDispose = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public LspServices(
        [ImportMany(ProtocolConstants.RoslynLspLanguagesContract)] IEnumerable<Lazy<ILspService, LspServiceMetadataView>> roslynLspServices,
        [ImportMany(ProtocolConstants.TypeScriptLanguageContract)] IEnumerable<Lazy<ILspService, LspServiceMetadataView>> typeScriptLspServices,
        [ImportMany(ProtocolConstants.RoslynLspLanguagesContract)] IEnumerable<Lazy<ILspServiceFactory, LspServiceMetadataView>> roslynLspServiceFactories,
        [ImportMany(ProtocolConstants.TypeScriptLanguageContract)] IEnumerable<Lazy<ILspServiceFactory, LspServiceMetadataView>> typeScriptLspServiceFactories)
    {
        _roslynLspServices = [.. roslynLspServices];
        _typeScriptLspServices = [.. typeScriptLspServices];
        _roslynLspServiceFactories = [.. roslynLspServiceFactories];
        _typeScriptLspServiceFactories = [.. typeScriptLspServiceFactories];
    }

    /// <summary>
    /// Seeds the per-server runtime context onto this scoped instance immediately after the scope is
    /// created. Selects the services exported for the server's contract and applies the
    /// "specific server kind overrides <see cref="WellKnownLspServerKinds.Any"/>" rule (keyed by
    /// exported type name).
    /// </summary>
    /// <remarks>
    /// This is safe to call after <see cref="ExportFactory{T}.CreateExport"/> and before any service is
    /// resolved because MEF is lazy: importing <see cref="LspServices"/> does not construct any scoped
    /// service, and no scoped service is built until a request calls <see cref="GetRequiredService{T}"/>.
    /// </remarks>
    public void Initialize(
        WellKnownLspServerKinds serverKind,
        FrozenDictionary<string, ImmutableArray<BaseService>> baseServices,
        IDisposable scopeLifetime)
    {
        Contract.ThrowIfFalse(_lazyMefLspServices is null, $"{nameof(LspServices)} has already been initialized.");

        _baseServices = baseServices;
        _scopeLifetime = scopeLifetime;
        ServerKind = serverKind;

        // Select the services and factories exported for this server's contract. The contract for a real
        // server kind is always either the Roslyn or TypeScript contract.
        var (mefLspServices, mefLspServiceFactories) = serverKind.GetContractName() == ProtocolConstants.TypeScriptLanguageContract
            ? (_typeScriptLspServices, _typeScriptLspServiceFactories)
            : (_roslynLspServices, _roslynLspServiceFactories);

        var serviceMap = new Dictionary<string, Lazy<ILspService, LspServiceMetadataView>>();
        var factoryProducedServiceNames = new HashSet<string>();

        // Add services from factories exported for this server kind.
        foreach (var lazyServiceFactory in mefLspServiceFactories.Where(f => f.Metadata.ServerKind == serverKind))
            AddSpecificService(new(() => lazyServiceFactory.Value.CreateILspService(this, serverKind), lazyServiceFactory.Metadata), isFactoryProduced: true);

        // Add services exported for this server kind.
        foreach (var lazyService in mefLspServices.Where(s => s.Metadata.ServerKind == serverKind))
            AddSpecificService(lazyService, isFactoryProduced: false);

        // Add services from factories exported for any (if there is not already an existing service for the specific server kind).
        foreach (var lazyServiceFactory in mefLspServiceFactories.Where(f => f.Metadata.ServerKind == WellKnownLspServerKinds.Any))
            TryAddAnyService(new(() => lazyServiceFactory.Value.CreateILspService(this, serverKind), lazyServiceFactory.Metadata), isFactoryProduced: true);

        // Add services exported for any (if there is not already an existing service for the specific server kind).
        foreach (var lazyService in mefLspServices.Where(s => s.Metadata.ServerKind == WellKnownLspServerKinds.Any))
            TryAddAnyService(lazyService, isFactoryProduced: false);

        _lazyMefLspServices = serviceMap.ToFrozenDictionary();
        _factoryProducedServiceNames = factoryProducedServiceNames.ToFrozenSet();

        void AddSpecificService(Lazy<ILspService, LspServiceMetadataView> serviceGetter, bool isFactoryProduced)
        {
            var metadata = serviceGetter.Metadata;
            Contract.ThrowIfFalse(metadata.ServerKind == serverKind);
            serviceMap.Add(metadata.TypeRef.TypeName, serviceGetter);
            if (isFactoryProduced)
                factoryProducedServiceNames.Add(metadata.TypeRef.TypeName);
        }

        void TryAddAnyService(Lazy<ILspService, LspServiceMetadataView> serviceGetter, bool isFactoryProduced)
        {
            var metadata = serviceGetter.Metadata;
            Contract.ThrowIfFalse(metadata.ServerKind == WellKnownLspServerKinds.Any);
            if (!serviceMap.TryGetValue(metadata.TypeRef.TypeName, out var existing))
            {
                serviceMap.Add(metadata.TypeRef.TypeName, serviceGetter);
                if (isFactoryProduced)
                    factoryProducedServiceNames.Add(metadata.TypeRef.TypeName);
            }
            else
            {
                // Make sure we're not trying to add a duplicate Any service, but otherwise we should skip adding
                // this service as we already have a more specific service available.
                Contract.ThrowIfTrue(existing.Metadata.ServerKind == WellKnownLspServerKinds.Any);
            }
        }
    }

    private FrozenDictionary<string, Lazy<ILspService, LspServiceMetadataView>> LazyMefLspServices
        => _lazyMefLspServices ?? throw new InvalidOperationException($"{nameof(LspServices)} has not been initialized.");

    public T GetRequiredService<T>() where T : notnull
    {
        var service = GetService<T>();
        Contract.ThrowIfNull(service, $"Missing required LSP service {typeof(T).FullName}");
        return service;
    }

    public T? GetService<T>() where T : notnull
    {
        var type = typeof(T);
        var typeName = type.FullName;
        Contract.ThrowIfNull(typeName);

        // Query for a service with an exact type match.
        var service = GetService(typeName);
        if (service is not null)
        {
            return (T)service;
        }

        // If given an interface, query for a service that implements that interface (this is how GetRequiredServices works)
        // Only allow this if there is exactly one service that implements the interface.
        return type.IsInterface
            ? GetRequiredServices<T>().SingleOrDefault()
            : default;
    }

    public IEnumerable<T> GetRequiredServices<T>()
    {
        // We provide this ILspServices instance as a service.
        if (typeof(T) == typeof(ILspServices))
        {
            yield return (T)(object)this;
        }

        foreach (var service in GetBaseServices<T>())
        {
            yield return service;
        }

        foreach (var service in GetMefServices<T>())
        {
            yield return service;
        }
    }

    public bool TryGetService(Type type, [NotNullWhen(true)] out object? service)
    {
        var typeName = type.FullName;
        Contract.ThrowIfNull(typeName);

        service = GetService(typeName);
        return service is not null;
    }

    private object? GetService(string typeName)
    {
        // We provide this ILspServices instance as a service.
        if (typeName == typeof(ILspServices).FullName)
        {
            return this;
        }

        // Check the base services first
        if (_baseServices.TryGetValue(typeName, out var baseServices))
        {
            // It's possible that there may be more than one base service registered for the same type,
            // such as IMethodHandler. If that's the case, we return null.
            return baseServices is [var baseService]
                ? baseService.GetInstance(this)
                : null;
        }

        if (LazyMefLspServices.TryGetValue(typeName, out var lazyService))
        {
            // If we are creating an ILspServiceFactory-produced service for the first time, we need to check
            // if it is disposable after creation and keep it around to dispose of on shutdown. These instances
            // are not MEF-managed parts. MEF-managed services (whether scoped to this server's sharing boundary
            // or globally shared) are disposed by the scope/container, so we do not track those here.
            var checkDisposal = _factoryProducedServiceNames.Contains(typeName) && !lazyService.IsValueCreated;

            var lspService = lazyService.Value;
            if (checkDisposal && lspService is IDisposable disposable)
            {
                lock (_gate)
                {
                    _servicesToDispose.Add(disposable);
                }
            }

            return lspService;
        }

        return null;
    }

    public ImmutableArray<(IMethodHandler? Instance, TypeRef HandlerTypeRef, ImmutableArray<MethodHandlerDetails> HandlerDetails)> GetMethodHandlers()
    {
        using var _ = ArrayBuilder<(IMethodHandler?, TypeRef, ImmutableArray<MethodHandlerDetails>)>.GetInstance(out var builder);

        // First, add any IMethodHandlers found in base services.
        foreach (var handler in GetBaseServices<IMethodHandler>())
        {
            var handlerType = handler.GetType();
            var methods = MethodHandlerDetails.From(handlerType);

            builder.Add((handler, TypeRef.From(handlerType), methods));
        }

        // Now, walk through our MEF services and add any IMethodHandlers.
        foreach (var lazyService in LazyMefLspServices.Values)
        {
            var metadata = lazyService.Metadata;

            if (metadata.HandlerDetails is { } handlerMethods)
            {
                builder.Add((null, metadata.TypeRef, handlerMethods));
            }
        }

        return builder.ToImmutableAndClear();
    }

    private ImmutableArray<T> GetBaseServices<T>()
    {
        var typeName = typeof(T).FullName;
        Contract.ThrowIfNull(typeName);

        return _baseServices.TryGetValue(typeName, out var baseServices)
            ? baseServices.SelectAsArray(s => (T)s.GetInstance(this))
            : [];
    }

    private IEnumerable<T> GetMefServices<T>()
    {
        foreach (var (typeName, lazyService) in LazyMefLspServices)
        {
            if (lazyService.Metadata.InterfaceNames.Contains(typeof(T).AssemblyQualifiedName!))
            {
                var serviceInstance = GetService(typeName);
                if (serviceInstance is not null)
                {
                    yield return (T)serviceInstance;
                }
                else
                {
                    throw new InvalidOperationException($"Could not construct service: {typeName}");
                }
            }
        }
    }

    public void Dispose()
    {
        ImmutableArray<IDisposable> disposableServices;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            disposableServices = [.. _servicesToDispose];
            _servicesToDispose.Clear();
        }

        // Dispose ILspServiceFactory-produced services (which are not MEF-managed parts) first, before
        // tearing down the scope they may depend on.
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

        // Tear down this server's sharing-boundary scope. This disposes all [Shared(...boundary...)] parts
        // created for this server, including this LspServices instance, which re-enters Dispose and no-ops
        // due to the _disposed guard above.
        _scopeLifetime?.Dispose();
    }
}
