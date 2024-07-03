﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// This is consumed as 'generated' code in a source package and therefore requires an explicit nullable enable
#nullable enable

using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

namespace Microsoft.CommonLanguageServerProtocol.Framework;

/// <summary>
/// A placeholder type to help handle parameterless messages and messages with no return value.
/// </summary>
internal sealed class NoValue
{
    public static NoValue Instance = new();
}

internal class QueueItem<TRequest, TRequestContext> : IQueueItem<TRequestContext>
{
    private readonly TRequest _request;
    private readonly ILspLogger _logger;
    private readonly AbstractRequestScope? _requestTelemetryScope;

    /// <summary>
    /// A task completion source representing the result of this queue item's work.
    /// This is the task that the client is waiting on.
    /// </summary>
    private readonly TaskCompletionSource<object?> _completionSource = new();

    public ILspServices LspServices { get; }

    public string MethodName { get; }

    public DelegatingEntryPoint<TRequestContext> EntryPoint { get; }

    public IMethodHandler DefaultHandler { get; }

    private QueueItem(
        string methodName,
        TRequest request,
        DelegatingEntryPoint<TRequestContext> entryPoint,
        ILspServices lspServices,
        ILspLogger logger,
        CancellationToken cancellationToken)
    {
        // Set the tcs state to cancelled if the token gets cancelled outside of our callback (for example the server shutting down).
        cancellationToken.Register(() => _completionSource.TrySetCanceled(cancellationToken));

        _logger = logger;
        EntryPoint = entryPoint;
        _request = request;
        LspServices = lspServices;

        MethodName = methodName;

        var telemetryService = lspServices.GetService<AbstractTelemetryService>();

        _requestTelemetryScope = telemetryService?.CreateRequestScope(methodName);
    }

    public static (QueueItem<TRequest, TRequestContext>, Task<object?>) Create(
        string methodName,
        TRequest request,
        DelegatingEntryPoint<TRequestContext> entryPoint,
        ILspServices lspServices,
        ILspLogger logger,
        CancellationToken cancellationToken)
    {
        var queueItem = new QueueItem<TRequest, TRequestContext>(
            methodName,
            request,
            entryPoint,
            lspServices,
            logger,
            cancellationToken);

        return (queueItem, queueItem._completionSource.Task);
    }

    public async Task<(TRequestContext Context, string Language, IMethodHandler handler, MethodInfo handlerEntryPoint)> ResolveQueueItemAsync(
        AbstractLanguageServer<TRequestContext> server,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _requestTelemetryScope?.RecordExecutionStart();

        // We have a bit of a catch-22.  In order to determine the handler, we need to determine the language, but
        // in order to get the language, we need to invoke a handler to retrieve the URI from the request.
        //
        // To resolve this, we assert upfront that all handlers for the same method have the same C# request type.
        // Then we use the default handler to resolve the language, at which point we can find the correct handler for all subsequent actions.
        //
        // This must be done serially inside the queue to ensure that requests that set the language for a URI are completed before the next requests run.
        var language = server.GetLanguageForRequest(MethodName, _request, DefaultHandler);

        var (handler, handlerMethodInfo) = EntryPoint.GetHandlerInfo(language);

        var requestContextFactory = LspServices.GetRequiredService<AbstractRequestContextFactory<TRequestContext>>();
        var context = await requestContextFactory.CreateRequestContextAsync(this, handler, _request, cancellationToken).ConfigureAwait(false);
        return (context, language, handler, handlerMethodInfo);
    }

    public async Task StartRequestAsync(string language, TRequestContext? context, IMethodHandler handler, MethodInfo methodInfo, CancellationToken cancellationToken)
    {
        var task = methodInfo.Invoke(this, [language, context, handler, cancellationToken]) as Task
                ?? throw new InvalidOperationException($"StartRequestAsync result task cannot be null");
        await task.ConfigureAwait(false);
    }

    /// <summary>
    /// Processes the queued request. Exceptions will be sent to the task completion source
    /// representing the task that the client is waiting for, then re-thrown so that
    /// the queue can correctly handle them depending on the type of request.
    /// </summary>
    private async Task StartRequestAsync<TResponse>(string language, TRequestContext? context, IMethodHandler handler, CancellationToken cancellationToken)
    {
        _logger.LogStartContext($"{MethodName}");
        _requestTelemetryScope?.UpdateLanguage(language);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (context is null)
            {
                // If we weren't able to get a corresponding context for this request (for example, we
                // couldn't map a doc request to a particular Document, or we couldn't find an appropriate
                // Workspace for a global operation), then just immediately complete the request with a
                // 'null' response.  Note: the lsp spec was checked to ensure that 'null' is valid for all
                // the requests this could happen for.  However, this assumption may not hold in the future.
                // If that turns out to be the case, we could defer to the individual handler to decide
                // what to do.
                _requestTelemetryScope?.RecordWarning($"Could not get request context for {MethodName}");
                _logger.LogWarning($"Could not get request context for {MethodName}");

                _completionSource.TrySetException(new InvalidOperationException($"Unable to create request context for {MethodName}"));
            }
            else if (handler is IRequestHandler<TRequest, TResponse, TRequestContext> requestHandler)
            {
                var result = await requestHandler.HandleRequestAsync(_request, context, cancellationToken).ConfigureAwait(false);

                _completionSource.TrySetResult(result);
            }
            else if (handler is IRequestHandler<TResponse, TRequestContext> parameterlessRequestHandler)
            {
                var result = await parameterlessRequestHandler.HandleRequestAsync(context, cancellationToken).ConfigureAwait(false);

                _completionSource.TrySetResult(result);
            }
            else if (handler is INotificationHandler<TRequest, TRequestContext> notificationHandler)
            {
                await notificationHandler.HandleNotificationAsync(_request, context, cancellationToken).ConfigureAwait(false);

                // We know that the return type of <see cref="INotificationHandler{TRequestType, RequestContextType}"/> will always be <see cref="VoidReturn" /> even if the compiler doesn't.
                _completionSource.TrySetResult((TResponse)(object)NoValue.Instance);
            }
            else if (handler is INotificationHandler<TRequestContext> parameterlessNotificationHandler)
            {
                await parameterlessNotificationHandler.HandleNotificationAsync(context, cancellationToken).ConfigureAwait(false);

                // We know that the return type of <see cref="INotificationHandler{TRequestType, RequestContextType}"/> will always be <see cref="VoidReturn" /> even if the compiler doesn't.
                _completionSource.TrySetResult((TResponse)(object)NoValue.Instance);
            }
            else
            {
                throw new NotImplementedException($"Unrecognized {nameof(IMethodHandler)} implementation {handler.GetType()}.");
            }
        }
        catch (OperationCanceledException ex)
        {
            // Record logs + metrics on cancellation.
            _requestTelemetryScope?.RecordCancellation();
            _logger.LogInformation($"{MethodName} - Canceled");

            _completionSource.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            // Record logs and metrics on the exception.
            // It's important that this can NEVER throw, or the queue will hang.
            _requestTelemetryScope?.RecordException(ex);
            _logger.LogException(ex);

            _completionSource.TrySetException(ex);
        }
        finally
        {
            _requestTelemetryScope?.Dispose();
            _logger.LogEndContext($"{MethodName}");
        }

        // Return the result of this completion source to the caller
        // so it can decide how to handle the result / exception.
        await _completionSource.Task.ConfigureAwait(false);
    }
}
