// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.ComponentModel.Composition.Hosting;
using System.Linq;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.LanguageServer
{
    internal class LspServices : IDisposable
    {
        public ImmutableDictionary<Type, Lazy<ILspService, LspServiceMetadataView>> LazyLspServices { get; }

        public LspServices(ExportProvider exportProvider, WellKnownLspServerKinds serverKind, ImmutableArray<Lazy<ILspService, LspServiceMetadataView>> baseServices)
        {
            // The act of calling get exports on an ILspService will instantiate new instances as long as they are not exported with the Shared attribute.
            // This is the key codepath that ensures each LSP server gets their own set of LSP services on initialization.
            var services = exportProvider.GetExports<ILspService, LspServiceMetadataView>(serverKind.GetContractName());
            var servicesFromFactories = exportProvider.GetExports<ILspServiceFactory, LspServiceMetadataView>(serverKind.GetContractName())
                .Select(lz => new Lazy<ILspService, LspServiceMetadataView>(() => lz.Value.CreateILspService(this, serverKind), lz.Metadata));

            // Combine the services and factories into one set of lazies.
            services = services.Concat(servicesFromFactories);

            // Make sure that we only include services exported for the specified server kind (or NotSpecified).
            services = services.Where(service => service.Metadata.ServerKind == serverKind || service.Metadata.ServerKind == WellKnownLspServerKinds.NotSpecified);

            // Include whatever base level services were passed in.
            services = services.Concat(baseServices);

            LazyLspServices = services.ToImmutableDictionary(service => service.Metadata.Type, service => service);
        }

        public T GetRequiredService<T>() where T : class, ILspService
        {
            var service = GetService<T>();
            Contract.ThrowIfNull(service, $"Missing required LSP service {typeof(T).FullName}");
            return service;
        }

        public T? GetService<T>() where T : class, ILspService
        {
            var type = typeof(T);
            if (LazyLspServices.TryGetValue(type, out var lazyService))
            {
                var lspService = lazyService.Value as T;
                Contract.ThrowIfNull(lspService, $"{lspService} could not be converted to {type.Name}");
                return lspService;
            }

            return null;
        }

        public void Dispose()
        {
            foreach (var service in LazyLspServices.Values)
            {
                if (service.IsValueCreated && service.Value is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }
    }
}
