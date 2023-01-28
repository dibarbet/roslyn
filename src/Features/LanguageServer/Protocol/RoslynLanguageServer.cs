// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.LanguageServer.Handler.ServerLifetime;
using Microsoft.CommonLanguageServerProtocol.Framework;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Roslyn.Utilities;
using StreamJsonRpc;

namespace Microsoft.CodeAnalysis.LanguageServer
{
    internal class RoslynLanguageServer : AbstractLanguageServer<RequestContext>
    {
        private readonly AbstractLspServiceProvider _lspServiceProvider;
        private readonly ImmutableDictionary<Type, Lazy<object>> _baseServices;
        private readonly WellKnownLspServerKinds _serverKind;

        public RoslynLanguageServer(
            AbstractLspServiceProvider lspServiceProvider,
            JsonRpc jsonRpc,
            ICapabilitiesProvider capabilitiesProvider,
            ILspServiceLogger logger,
            HostServices hostServices,
            ImmutableArray<string> supportedLanguages,
            WellKnownLspServerKinds serverKind)
            : base(jsonRpc, logger)
        {
            _lspServiceProvider = lspServiceProvider;
            _serverKind = serverKind;

            // Create services that require base dependencies (jsonrpc) or are more complex to create to the set manually.
            _baseServices = GetBaseServices(jsonRpc, logger, capabilitiesProvider, hostServices, serverKind, supportedLanguages);

            // This spins up the queue and ensure the LSP is ready to start receiving requests
            Initialize();
        }

        protected override ILspServices ConstructLspServices()
        {
            return _lspServiceProvider.CreateServices(_serverKind, _baseServices);
        }

        protected override IRequestExecutionQueue<RequestContext> ConstructRequestExecutionQueue()
        {
            var provider = GetLspServices().GetRequiredService<IRequestExecutionQueueProvider<RequestContext>>();
            return provider.CreateRequestExecutionQueue(this, _logger, GetHandlerProvider());
        }

        /// <summary>
        /// Provides any base services that either require non-MEF and non-LSP service objects
        /// in their constructor or that do not inherit from <see cref="ILspService"/>.
        /// For example the client language server manager requires the JsonRpc
        /// instance in order to send messages - this is only available upon constructing the connection.
        /// </summary>
        private ImmutableDictionary<Type, Lazy<object>> GetBaseServices(
            JsonRpc jsonRpc,
            ILspServiceLogger logger,
            ICapabilitiesProvider capabilitiesProvider,
            HostServices hostServices,
            WellKnownLspServerKinds serverKind,
            ImmutableArray<string> supportedLanguages)
        {
            var baseServices = new Dictionary<Type, Lazy<object>>();
            var clientLanguageServerManager = new ClientLanguageServerManager(jsonRpc);

            AddBaseService<LspMiscellaneousFilesWorkspace>(new LspMiscellaneousFilesWorkspace(hostServices));
            AddBaseService<IClientLanguageServerManager>(clientLanguageServerManager);
            AddBaseService<ILspLogger>(logger);
            AddBaseService<ILspServiceLogger>(logger);
            AddBaseService<ICapabilitiesProvider>(capabilitiesProvider);
            AddBaseService(new ServerInfoProvider(serverKind, supportedLanguages));

            // These are clasp types and therefore do not inherit from ILspService.
            AddLazyBaseService<IRequestExecutionQueue<RequestContext>>(new Lazy<object>(GetRequestExecutionQueue));
            AddLazyBaseService<IRequestContextFactory<RequestContext>>(new Lazy<object>(() => new RequestContextFactory(GetLspServices())));

            return baseServices.ToImmutableDictionary();

            void AddBaseService<T>(T instance) where T : class
            {
                AddLazyBaseService<T>(new Lazy<object>(() => instance));
            }

            void AddLazyBaseService<T>(Lazy<object> lazyService)
            {
                baseServices.Add(typeof(T), lazyService);
            }
        }
    }
}
