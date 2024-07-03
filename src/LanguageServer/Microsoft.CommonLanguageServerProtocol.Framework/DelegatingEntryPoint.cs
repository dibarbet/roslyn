// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// This is consumed as 'generated' code in a source package and therefore requires an explicit nullable enable
#nullable enable

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.CommonLanguageServerProtocol.Framework;

internal abstract class DelegatingEntryPoint<TRequestContext>
{
    private static readonly Type s_noValueType = NoValue.Instance.GetType();

    private readonly AbstractLanguageServer<TRequestContext> _server;

    /// <summary>
    /// Map of language to info needed to invoke the actual handling of the request using the handler's specified request and response types.
    /// 
    /// Lazy to avoid instantiating the language specific handler until there is a request for this method and language.
    /// </summary>
    private readonly FrozenDictionary<string, (RequestHandlerMetadata Metadata, Lazy<(MethodInfo StartRequestAsyncMethodInfo, IMethodHandler HandlerInstance)> LazyData)> _languageEntryPoint;

    protected readonly string _method;
    protected readonly AbstractTypeRefResolver _typeRefResolver;

    /// <summary>
    /// Handler info for the default handler, including the generic method to invoke the queue, the concrete request type (deserialization)
    /// and the instantiated handler.
    /// 
    /// Lazy to avoid resolving the type information and handler until there is a request for this method.
    /// </summary>
    protected readonly Lazy<(MethodInfo QueueExecuteMethodInfo, RequestHandlerMetadata Metadata, IMethodHandler Handler)> DefaultHandlerInfo;

    private static readonly MethodInfo s_executeAsync = typeof(RequestExecutionQueue<TRequestContext>).GetMethod(nameof(RequestExecutionQueue<TRequestContext>.ExecuteAsync))!;
    private static readonly MethodInfo s_startRequestAsync = typeof(IQueueItem<TRequestContext>).GetMethod(nameof(IQueueItem<TRequestContext>.StartRequestAsync))!;

    public DelegatingEntryPoint(string method, AbstractTypeRefResolver typeRefResolver, IGrouping<string, RequestHandlerMetadata> handlersForMethod, AbstractHandlerProvider handlerProvider, AbstractLanguageServer<TRequestContext> server)
    {
        _method = method;
        _typeRefResolver = typeRefResolver;
        _server = server;
        var handlerEntryPoints = new Dictionary<string, (RequestHandlerMetadata Metadata, Lazy<(MethodInfo StartRequestAsyncMethodInfo, IMethodHandler HandlerInstance)>)>();
        foreach (var metadata in handlersForMethod)
        {
            var lazyData = new Lazy<(MethodInfo StartRequestAsyncMethodInfo, IMethodHandler HandlerInstance)>(() =>
            {
                var requestType = metadata.RequestTypeRef is TypeRef requestTypeRef
                    ? _typeRefResolver.Resolve(requestTypeRef) ?? s_noValueType
                    : s_noValueType;
                var responseType = metadata.ResponseTypeRef is TypeRef responseTypeRef
                    ? _typeRefResolver.Resolve(responseTypeRef) ?? s_noValueType
                    : s_noValueType;

                var startAsync = s_startRequestAsync.MakeGenericMethod(requestType, responseType);
                var handlerInstance = handlerProvider.GetMethodHandler(method, metadata.RequestTypeRef, metadata.ResponseTypeRef, metadata.Language);

                return (startAsync, handlerInstance);
            });

            handlerEntryPoints[metadata.Language] = (metadata, lazyData);
        }

        _languageEntryPoint = handlerEntryPoints.ToFrozenDictionary();

        DefaultHandlerInfo = new(() =>
        {
            // Get the default handler info.  The default handler is either the single handler for the language (e.g. a custom Razor method)
            // or the default language handler.  We verified previously that if there is more than one handler for a method there must be a default handler.
            (RequestHandlerMetadata Metadata, Lazy<(MethodInfo StartRequestAsyncMethodInfo, IMethodHandler HandlerInstance)> HandlerInfo) handlerEntry;
            if (_languageEntryPoint.Values.Length == 1)
            {
                handlerEntry = _languageEntryPoint.Values.Single();
            }
            else
            {
                // We verified in construction that there must be a default handler if there is more than one handlers for the same method.
                handlerEntry = _languageEntryPoint[LanguageServerConstants.DefaultLanguageName];
            }

            var requestType = handlerEntry.Metadata.RequestTypeRef is TypeRef requestTypeRef
                    ? _typeRefResolver.Resolve(requestTypeRef) ?? s_noValueType
                    : s_noValueType;

            var methodInfo = s_executeAsync.MakeGenericMethod(requestType);

            return (methodInfo, handlerEntry.Metadata, handlerEntry.HandlerInfo.Value.HandlerInstance);
        });
    }

    public abstract MethodInfo GetEntryPoint(bool hasParameter);

    public (IMethodHandler Handler, MethodInfo StartRequestMethodInfo) GetHandlerInfo(string language)
    {
        // Now we know the language so we can return either the matching handler for the language or the default handler (if no language specific one exists).
        if (_languageEntryPoint.TryGetValue(language, out var data) ||
            _languageEntryPoint.TryGetValue(LanguageServerConstants.DefaultLanguageName, out data))
        {
            return (data.LazyData.Value.HandlerInstance, data.LazyData.Value.StartRequestAsyncMethodInfo);
        }

        throw new InvalidOperationException($"No handler exists for {_method} and language {language}");
    }

    /// <summary>
    /// We reflection invoke the <see cref="RequestExecutionQueue{TRequestContext}.ExecuteAsync{TRequest}(TRequest, string, IMethodHandler, DelegatingEntryPoint, ILspServices, CancellationToken)"/>
    /// using the default handler information.
    /// 
    /// This allows the queue to resolve the language and create the request context up front.  Then later on when we actually invoke the request
    /// we can invoke it using the actual request / response type for the handler matching the language.
    /// </summary>
    protected async Task<object?> AddToQueueAsync(object deserializedRequest, ILspServices lspServices, CancellationToken cancellationToken)
    {
        var task = DefaultHandlerInfo.Value.QueueExecuteMethodInfo.Invoke(this, [deserializedRequest, _method, DefaultHandlerInfo.Value.Handler, this, lspServices, cancellationToken]) as Task
            ?? throw new InvalidOperationException($"ExecuteAsync result task cannot be null");
        await task.ConfigureAwait(false);
        var resultProperty = task.GetType().GetProperty("Result") ?? throw new InvalidOperationException("Result property on task cannot be null");
        var result = resultProperty.GetValue(task);
        if (result == NoValue.Instance)
        {
            return null;
        }
        else
        {
            return result;
        }
    }
}
