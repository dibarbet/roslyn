// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// This is consumed as 'generated' code in a source package and therefore requires an explicit nullable enable
#nullable enable

using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;

namespace Microsoft.CommonLanguageServerProtocol.Framework;

internal abstract class SystemTextJsonLanguageServer<TRequestContext>(
    JsonRpc jsonRpc, JsonSerializerOptions options, ILspLogger logger, AbstractTypeRefResolver? typeRefResolver = null)
    : AbstractLanguageServer<TRequestContext>(jsonRpc, logger, typeRefResolver)
{
    /// <summary>
    /// JsonSerializer options used by streamjsonrpc (and for serializing / deserializing the requests to streamjsonrpc).
    /// These options are specifically from the <see cref="StreamJsonRpc.SystemTextJsonFormatter"/> that added the exotic type converters.
    /// </summary>
    private readonly JsonSerializerOptions _jsonSerializerOptions = options;

    protected override DelegatingEntryPoint CreateDelegatingEntryPoint(string method, IGrouping<string, RequestHandlerMetadata> handlersForMethod, AbstractHandlerProvider handlerProvider)
    {
        return new SystemTextJsonDelegatingEntryPoint(method, handlersForMethod, this, handlerProvider);
    }

    private sealed class SystemTextJsonDelegatingEntryPoint(
        string method,
        IGrouping<string, RequestHandlerMetadata> handlersForMethod,
        SystemTextJsonLanguageServer<TRequestContext> target,
        AbstractHandlerProvider handlerProvider) : DelegatingEntryPoint(method, target.TypeRefResolver, handlersForMethod, handlerProvider)
    {
        private static readonly MethodInfo s_parameterlessEntryPoint = typeof(SystemTextJsonDelegatingEntryPoint).GetMethod(nameof(SystemTextJsonDelegatingEntryPoint.ExecuteRequest0Async), BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly MethodInfo s_entryPoint = typeof(SystemTextJsonDelegatingEntryPoint).GetMethod(nameof(SystemTextJsonDelegatingEntryPoint.ExecuteRequestAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

        public override MethodInfo GetEntryPoint(bool hasParameter)
        {
            return hasParameter ? s_entryPoint : s_parameterlessEntryPoint;
        }

        /// <summary>
        /// StreamJsonRpc entry point for handlers with no parameters.
        /// Unlike Newtonsoft, we have to differentiate instead of using default parameters.
        /// </summary>
        private Task<object?> ExecuteRequest0Async<TResponse>(CancellationToken cancellationToken = default)
        {
            return ExecuteRequestAsync<NoValue, TResponse>(null, cancellationToken);
        }

        /// <summary>
        /// StreamJsonRpc entry point for handlers with parameters (and any response) type.
        /// </summary>
        private Task<object?> ExecuteRequestAsync<TRequest, TResponse>(JsonElement? request, CancellationToken cancellationToken = default)
        {
            var queue = target.GetRequestExecutionQueue();
            var lspServices = target.GetLspServices();

            // Deserialize the request using the default language type.
            // This means that all language specific handlers must have matching request types to the default language handler.
            // This is enforced for Razor / XAML as they rely on the Roslyn protocol type definitions.
            var defaultMetadata = _languageEntryPoint[LanguageServerConstants.DefaultLanguageName].Metadata;
            var deserializedRequest = DeserializeRequest(request, defaultMetadata, target._jsonSerializerOptions);

            return queue.ExecuteAsync(deserializedRequest, _method, this, lspServices, cancellationToken);
        }

        private object DeserializeRequest(JsonElement? request, RequestHandlerMetadata metadata, JsonSerializerOptions options)
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

            return JsonSerializer.Deserialize(request.Value, requestType, options)
                ?? throw new InvalidOperationException($"Unable to deserialize {request} into {requestTypeRef} for {metadata.HandlerDescription}");
        }
    }
}
