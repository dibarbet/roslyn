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
    /// <summary>
    /// Delegate representing an invocation to <see cref="IRequestHandler{TRequest, TResponse, TRequestContext}"/>
    /// </summary>
    private delegate Task<object?> InvokeRequest<TRequest>(TRequest request, TRequestContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Delegate representing an invocation to <see cref="IRequestHandler{TResponse, TRequestContext}"/>
    /// </summary>
    private delegate Task<object?> InvokeParameterlessRequest(TRequestContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Delegate representing an invocation to <see cref="INotificationHandler{TRequest, TRequestContext}"/>
    /// </summary>
    private delegate Task InvokeNotification<TRequest>(TRequest request, TRequestContext requestContext, CancellationToken cancellationToken);

    /// <summary>
    /// Delegate representing an invocation to <see cref="INotificationHandler{TRequestContext}"/>
    /// </summary>
    private delegate Task InvokeParameterlessNotification(TRequestContext requestContext, CancellationToken cancellationToken);

    private static readonly Type s_noValueType = NoValue.Instance.GetType();

    private static readonly MethodInfo s_executeAsync = typeof(RequestExecutionQueue<TRequestContext>).GetMethod(nameof(RequestExecutionQueue<TRequestContext>.ExecuteAsync))!;

    private readonly AbstractLanguageServer<TRequestContext> _server;
    private readonly string _method;

    /// <summary>
    /// Map of language to handler and delegate info needed to invoke the actual request handling.
    /// Lazy to avoid instantiating the language specific handler until there is a request for this method and language.
    /// </summary>
    /// <remarks>
    /// Because some concrete handler implementations are defined with both TRequest and TResponse generic type parameters,
    /// it is necessary for us to invoke the handler with both type arguments.  However, we would like to avoid
    /// requiring all language specific handlers for a method to have exactly the same return types.
    /// 
    /// As such, we store delegates that invoke the handler using concrete TRequest types, but always return object? (or nothing).
    /// </remarks>
    private readonly FrozenDictionary<string, Lazy<(IMethodHandler, Delegate)>> _languageEntryPoint;

    /// <summary>
    /// Handler info for the default handler, including the generic method to invoke the queue, the concrete request type (deserialization)
    /// and the instantiated handler.
    /// 
    /// Lazy to avoid resolving the type information and handler until there is a request for this method.
    /// </summary>
    protected readonly Lazy<(MethodInfo QueueExecuteMethodInfo, RequestHandlerMetadata Metadata, IMethodHandler Handler)> DefaultHandlerInfo;

    protected readonly AbstractTypeRefResolver TypeRefResolver;

    public DelegatingEntryPoint(string method, AbstractTypeRefResolver typeRefResolver, IGrouping<string, RequestHandlerMetadata> handlersForMethod, AbstractHandlerProvider handlerProvider, AbstractLanguageServer<TRequestContext> server)
    {
        _method = method;
        TypeRefResolver = typeRefResolver;
        _server = server;

        var handlerEntryPoints = new Dictionary<string, Lazy<(IMethodHandler, Delegate)>>();
        foreach (var metadata in handlersForMethod)
        {
            var lazyData = new Lazy<(IMethodHandler, Delegate)>(() =>
            {
                var requestType = metadata.RequestTypeRef is TypeRef requestTypeRef
                    ? TypeRefResolver.Resolve(requestTypeRef) ?? s_noValueType
                    : s_noValueType;
                var responseType = metadata.ResponseTypeRef is TypeRef responseTypeRef
                    ? TypeRefResolver.Resolve(responseTypeRef) ?? s_noValueType
                    : s_noValueType;

                var handlerInstance = handlerProvider.GetMethodHandler(method, metadata.RequestTypeRef, metadata.ResponseTypeRef, metadata.Language);

                Delegate handlerDelegate;
                if (responseType != s_noValueType)
                {
                    if (requestType != null)
                    {
                        //This is an IRequestHandler<TRequest, TResponse, TRequestContext.
                        var handlerInfo = typeof(IRequestHandler<,,>).MakeGenericType(requestType, responseType).GetMethod(nameof(IRequestHandler<object, object, TRequestContext>.HandleRequestAsync))!;
                        var delegateType = typeof(InvokeRequest<>).MakeGenericType(requestType);
                        handlerDelegate = handlerInfo.CreateDelegate(delegateType, handlerInstance);
                    }
                    else
                    {
                        //This is an IRequestHandler<TResponse, TRequestContext.
                        var handlerInfo = typeof(IRequestHandler<,>).MakeGenericType(responseType).GetMethod(nameof(IRequestHandler<object, TRequestContext>.HandleRequestAsync))!;
                        var delegateType = typeof(InvokeParameterlessRequest).MakeGenericType();
                        handlerDelegate = handlerInfo.CreateDelegate(delegateType, handlerInstance);
                    }
                }
                else
                {
                    if (requestType != null)
                    {
                        //This is an INotificationHandler<TRequest, TRequestContext.
                        var handlerInfo = typeof(INotificationHandler<,>).MakeGenericType(requestType, responseType).GetMethod(nameof(INotificationHandler<object, TRequestContext>.HandleNotificationAsync))!;
                        var delegateType = typeof(InvokeNotification<>).MakeGenericType(requestType);
                        handlerDelegate = handlerInfo.CreateDelegate(delegateType, handlerInstance);
                    }
                    else
                    {
                        //This is an INotificationHandler<TRequestContext.
                        var handlerInfo = typeof(INotificationHandler<>).MakeGenericType(responseType).GetMethod(nameof(INotificationHandler<TRequestContext>.HandleNotificationAsync))!;
                        var delegateType = typeof(InvokeParameterlessNotification).MakeGenericType();
                        handlerDelegate = handlerInfo.CreateDelegate(delegateType, handlerInstance);
                    }
                }

                return (handlerInstance, handlerDelegate);
            });

            handlerEntryPoints[metadata.Language] = lazyData;
        }

        _languageEntryPoint = handlerEntryPoints.ToFrozenDictionary();

        DefaultHandlerInfo = new(() =>
        {
            // Get the default handler info.  The default handler is either the single handler for the language (e.g. a custom Razor method)
            // or the default language handler.
            RequestHandlerMetadata defaultMetadata;
            if (handlersForMethod.Count() == 1)
            {
                defaultMetadata = handlersForMethod.Single();
            }
            else
            {
                // We verified in construction that there must be a default handler if there is more than one handlers for the same method.
                defaultMetadata = handlersForMethod.Where(m => m.Language == LanguageServerConstants.DefaultLanguageName).Single();
            }

            var requestType = defaultMetadata.RequestTypeRef is TypeRef requestTypeRef
                    ? TypeRefResolver.Resolve(requestTypeRef) ?? s_noValueType
                    : s_noValueType;

            var methodInfo = s_executeAsync.MakeGenericMethod(requestType);

            var handler = handlerProvider.GetMethodHandler(defaultMetadata.MethodName, defaultMetadata.RequestTypeRef, defaultMetadata.ResponseTypeRef, defaultMetadata.Language);

            return (methodInfo, defaultMetadata, handler);
        });
    }

    public abstract MethodInfo GetEntryPoint(bool hasParameter);

    public (IMethodHandler Handler, Delegate InvokeHandler) GetHandlerInfo(string language)
    {
        // Now we know the language so we can return either the matching handler for the language or the default handler (if no language specific one exists).
        if (_languageEntryPoint.TryGetValue(language, out var data) ||
            _languageEntryPoint.TryGetValue(LanguageServerConstants.DefaultLanguageName, out data))
        {
            return data.Value;
        }

        throw new InvalidOperationException($"No handler exists for {_method} and language {language}");
    }

    public async Task<object?> InvokeHandlerDelegateAsync<TRequest>(Delegate handlerDelegate, TRequest request, TRequestContext context, CancellationToken cancellationToken)
    {
        if (handlerDelegate is InvokeRequest<TRequest> invokeRequest)
        {
            return invokeRequest(request, context, cancellationToken);
        }
        else if (handlerDelegate is InvokeParameterlessRequest invokeParameterlessRequest)
        {
            return invokeParameterlessRequest(context, cancellationToken);
        }
        else if (handlerDelegate is InvokeNotification<TRequest> invokeNotification)
        {
            await invokeNotification(request, context, cancellationToken).ConfigureAwait(false);
            return NoValue.Instance;
        }
        else if (handlerDelegate is InvokeParameterlessNotification invokeParameterlessNotification)
        {
            await invokeParameterlessNotification(context, cancellationToken).ConfigureAwait(false);
            return NoValue.Instance;
        }
        else
        {
            throw new InvalidOperationException("Unexpected handler delegate type: " + handlerDelegate.GetType());
        }
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
