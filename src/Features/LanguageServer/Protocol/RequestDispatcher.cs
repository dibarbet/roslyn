﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.LanguageServer.Handler.Commands;
using Newtonsoft.Json;
using Roslyn.Utilities;
using LSP = Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer
{
    internal record struct RequestHandlerMetadata(string MethodName, Type RequestType, Type ResponseType);

    /// <summary>
    /// Aggregates handlers for the specified languages and dispatches LSP requests
    /// to the appropriate handler for the request.
    /// </summary>
    internal class RequestDispatcher
    {
        private readonly ImmutableDictionary<RequestHandlerMetadata, Lazy<IRequestHandler>> _requestHandlers;

        public RequestDispatcher(ImmutableArray<Lazy<AbstractRequestHandlerProvider, RequestHandlerProviderMetadataView>> requestHandlerProviders, ImmutableArray<string> languageNames)
        {
            _requestHandlers = CreateMethodToHandlerMap(requestHandlerProviders.Where(rh => languageNames.All(languageName => rh.Metadata.LanguageNames.Contains(languageName))));
        }

        private static ImmutableDictionary<RequestHandlerMetadata, Lazy<IRequestHandler>> CreateMethodToHandlerMap(
            IEnumerable<Lazy<AbstractRequestHandlerProvider, RequestHandlerProviderMetadataView>> requestHandlerProviders)
        {
            var requestHandlerDictionary = ImmutableDictionary.CreateBuilder<RequestHandlerMetadata, Lazy<IRequestHandler>>();

            // Store the request handlers in a dictionary from request name to handler instance.
            foreach (var handlerProvider in requestHandlerProviders)
            {
                var handlerMetadata = handlerProvider.Metadata.HandlerMetadata;
                // Instantiate all the providers as one lazy object and re-use it for all methods that the provider provides handlers for.
                // This ensures 2 things:
                // 1.  That the handler provider is not instantiated (and therefore its dependencies are not) until a handler it provides is needed.
                // 2.  That the handler provider's CreateRequestHandlers is only called once and always returns the same handler instances.
                var lazyProviders = new Lazy<ImmutableDictionary<string, IRequestHandler>>(() => handlerProvider.Value.CreateRequestHandlers().ToImmutableDictionary(p => p.Method, p => p, StringComparer.OrdinalIgnoreCase));

                foreach (var (method, handlerType) in handlerMetadata)
                {
                    var (requestType, responseType) = ConvertHandlerTypeToRequestResponseTypes(handlerType);

                    // Using the lazy set of handlers, create a lazy instance that will resolve the set of handlers for the provider
                    // and then lookup the correct handler for the specified method.
                    requestHandlerDictionary.Add(new RequestHandlerMetadata(method, requestType, responseType), new Lazy<IRequestHandler>(() => lazyProviders.Value[method]));
                }
            }

            return requestHandlerDictionary.ToImmutable();
        }

        /// <summary>
        /// Retrieves the generic argument information from the request handler type without instantiating it.
        /// </summary>
        private static (Type requestType, Type responseType) ConvertHandlerTypeToRequestResponseTypes(Type handlerType)
        {
            var requestHandlerGenericType = handlerType.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)).SingleOrDefault();
            Contract.ThrowIfNull(requestHandlerGenericType, $"Provided handler type {handlerType.FullName} does not implement IRequestHandler<,>");

            var genericArguments = requestHandlerGenericType.GetGenericArguments();
            Contract.ThrowIfFalse(genericArguments.Length == 2, $"Provided handler type {handlerType.FullName} does not have exactly two generic arguments");
            var requestType = genericArguments[0];
            var responseType = genericArguments[1];

            return (requestType, responseType);
        }

        public async Task<TResponseType?> ExecuteRequestAsync<TRequestType, TResponseType>(
            string methodName,
            TRequestType request,
            LSP.ClientCapabilities clientCapabilities,
            string? clientName,
            RequestExecutionQueue queue,
            CancellationToken cancellationToken) where TRequestType : class
        {
            if (request is LSP.ExecuteCommandParams executeCommandRequest)
            {
                // If we have a workspace/executeCommand request, get the request name from the command name.
                methodName = AbstractExecuteWorkspaceCommandHandler.GetRequestNameForCommandName(executeCommandRequest.Command);
            }

            // Get the handler matching the requested method.
            var requestHandlerMetadata = new RequestHandlerMetadata(methodName, typeof(TRequestType), typeof(TResponseType));

            var handler = _requestHandlers[requestHandlerMetadata].Value; ;

            var mutatesSolutionState = handler.MutatesSolutionState;
            var requiresLspSolution = handler.RequiresLSPSolution;

            var strongHandler = (IRequestHandler<TRequestType, TResponseType>?)handler;
            Contract.ThrowIfNull(strongHandler, string.Format("Request handler not found for method {0}", methodName));

            var result = await ExecuteRequestAsync(queue, mutatesSolutionState, requiresLspSolution, strongHandler, request, clientCapabilities, clientName, methodName, cancellationToken).ConfigureAwait(false);
            return result;
        }

        protected virtual Task<TResponseType?> ExecuteRequestAsync<TRequestType, TResponseType>(
            RequestExecutionQueue queue,
            bool mutatesSolutionState,
            bool requiresLSPSolution,
            IRequestHandler<TRequestType, TResponseType> handler,
            TRequestType request,
            LSP.ClientCapabilities clientCapabilities,
            string? clientName,
            string methodName,
            CancellationToken cancellationToken) where TRequestType : class
        {
            return queue.ExecuteAsync(mutatesSolutionState, requiresLSPSolution, handler, request, clientCapabilities, clientName, methodName, cancellationToken);
        }

        public ImmutableArray<RequestHandlerMetadata> GetRegisteredMethods()
        {
            return _requestHandlers.Keys.ToImmutableArray();
        }

        internal TestAccessor GetTestAccessor()
            => new TestAccessor(this);

        internal readonly struct TestAccessor
        {
            private readonly RequestDispatcher _requestDispatcher;

            public TestAccessor(RequestDispatcher requestDispatcher)
                => _requestDispatcher = requestDispatcher;

            public IRequestHandler<RequestType, ResponseType> GetHandler<RequestType, ResponseType>(string methodName)
                => (IRequestHandler<RequestType, ResponseType>)_requestDispatcher._requestHandlers.Single(handler => handler.Value.Value.Method == methodName).Value.Value;
        }
    }
}
