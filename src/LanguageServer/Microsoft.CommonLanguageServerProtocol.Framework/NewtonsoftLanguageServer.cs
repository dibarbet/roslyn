// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// This is consumed as 'generated' code in a source package and therefore requires an explicit nullable enable
#nullable enable

using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace Microsoft.CommonLanguageServerProtocol.Framework;

/// <summary>
/// Basic implementation of <see cref="AbstractLanguageServer{TRequestContext}"/> using Newtonsoft for serialization.
/// </summary>
internal abstract class NewtonsoftLanguageServer<TRequestContext>(
    JsonRpc jsonRpc, JsonSerializer jsonSerializer, ILspLogger logger, AbstractTypeRefResolver? typeRefResolver = null)
    : AbstractLanguageServer<TRequestContext>(jsonRpc, logger, typeRefResolver)
{
    private readonly JsonSerializer _jsonSerializer = jsonSerializer;

    protected override DelegatingEntryPoint CreateDelegatingEntryPoint(string method, IGrouping<string, RequestHandlerMetadata> handlersForMethod, AbstractHandlerProvider handlerProvider)
    {
        return new NewtonsoftDelegatingEntryPoint(method, handlersForMethod, this, handlerProvider);
    }

    protected virtual string GetLanguageForRequest(string methodName, JToken? parameters)
    {
        Logger.LogInformation($"Using default language handler for {methodName}");
        return LanguageServerConstants.DefaultLanguageName;
    }

    private class NewtonsoftDelegatingEntryPoint(
        string method,
        IGrouping<string, RequestHandlerMetadata> handlersForMethod,
        NewtonsoftLanguageServer<TRequestContext> target,
        AbstractHandlerProvider handlerProvider) : DelegatingEntryPoint(method, target.TypeRefResolver, handlersForMethod, handlerProvider)
    {
        private static readonly MethodInfo s_entryPoint = typeof(NewtonsoftDelegatingEntryPoint).GetMethod(nameof(NewtonsoftDelegatingEntryPoint.ExecuteRequestAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

        public override MethodInfo GetEntryPoint(bool hasParameter)
        {
            return s_entryPoint;
        }

        /// <summary>
        /// StreamJsonRpc entry point for all handler methods.
        /// The optional parameters allow StreamJsonRpc to call into the same method for any kind of request / notification (with any number of params or response).
        /// </summary>
        private Task<object?> ExecuteRequestAsync(JToken? request = null, CancellationToken cancellationToken = default)
        {
            var queue = target.GetRequestExecutionQueue();
            var lspServices = target.GetLspServices();

            // Deserialize the request using the default language type.
            // This means that all language specific handlers must have matching request types to the default language handler.
            // This is enforced for Razor / XAML as they rely on the Roslyn protocol type definitions.
            var defaultMetadata = _languageEntryPoint[LanguageServerConstants.DefaultLanguageName].Metadata;
            var requestObject = DeserializeRequest(request, defaultMetadata, target._jsonSerializer);

            return queue.ExecuteAsync(requestObject, _method, this, lspServices, cancellationToken);
        }

        private object DeserializeRequest(JToken? request, RequestHandlerMetadata metadata, JsonSerializer jsonSerializer)
        {
            var requestTypeRef = metadata.RequestTypeRef;

            if (request is null)
            {
                if (requestTypeRef is not null)
                {
                    throw new InvalidOperationException($"Handler {metadata.HandlerDescription} requires request parameters but received none");
                }

                return NoValue.Instance;
            }

            // request is not null
            if (requestTypeRef is null)
            {
                throw new InvalidOperationException($"Handler {metadata.HandlerDescription} does not accept parameters, but received some.");
            }

            var requestType = _typeRefResolver.Resolve(requestTypeRef)
                ?? throw new InvalidOperationException($"Could not resolve type: '{requestTypeRef}'");

            return request.ToObject(requestType, jsonSerializer)
                ?? throw new InvalidOperationException($"Unable to deserialize {request} into {requestTypeRef} for {metadata.HandlerDescription}");
        }
    }
}
