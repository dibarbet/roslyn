﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// This is consumed as 'generated' code in a source package and therefore requires an explicit nullable enable
#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;

namespace Microsoft.CommonLanguageServerProtocol.Framework;

#if BINARY_COMPAT // TODO - Remove with https://github.com/dotnet/roslyn/issues/72251
public abstract class AbstractLanguageServer<TRequestContext>
#else
internal abstract class AbstractLanguageServer<TRequestContext>
#endif
{
    private readonly JsonRpc _jsonRpc;
#pragma warning disable IDE1006 // Naming Styles - Required for API compat, TODO - https://github.com/dotnet/roslyn/issues/72251
    protected readonly ILspLogger _logger;
#pragma warning restore IDE1006 // Naming Styles

    protected readonly IProtocolSerializer ProtocolSerializer;

    /// <summary>
    /// These are lazy to allow implementations to define custom variables that are used by
    /// <see cref="ConstructRequestExecutionQueue"/> or <see cref="ConstructLspServices"/>
    /// </summary>
    private readonly Lazy<IRequestExecutionQueue<TRequestContext>> _queue;
    private readonly Lazy<ILspServices> _lspServices;

    public bool IsInitialized { get; private set; }

    /// <summary>
    /// Ensures that we only run shutdown and exit code once in order.
    /// Guards access to <see cref="_shutdownRequestTask"/> and <see cref="_exitNotificationTask"/>
    /// </summary>
    private readonly object _lifeCycleLock = new();

    /// <summary>
    /// Task representing the work done on LSP server shutdown.
    /// </summary>
    private Task? _shutdownRequestTask;

    /// <summary>
    /// Task representing the work down on LSP exit.
    /// </summary>
    private Task? _exitNotificationTask;

    /// <summary>
    /// Task completion source that is started when the server starts and completes when the server exits.
    /// Used when callers need to wait for the server to cleanup.
    /// </summary>
    private readonly TaskCompletionSource<object?> _serverExitedSource = new();

    protected AbstractLanguageServer(
        JsonRpc jsonRpc,
        IProtocolSerializer jsonSerializer,
        ILspLogger logger)
    {
        _logger = logger;
        _jsonRpc = jsonRpc;
        ProtocolSerializer = jsonSerializer;

        _jsonRpc.AddLocalRpcTarget(this);
        _jsonRpc.Disconnected += JsonRpc_Disconnected;
        _lspServices = new Lazy<ILspServices>(() => ConstructLspServices());
        _queue = new Lazy<IRequestExecutionQueue<TRequestContext>>(() => ConstructRequestExecutionQueue());
    }

    /// <summary>
    /// Initializes the LanguageServer.
    /// </summary>
    /// <remarks>Should be called at the bottom of the implementing constructor or immediately after construction.</remarks>
    public void Initialize()
    {
        GetRequestExecutionQueue();
    }

    /// <summary>
    /// Extension point to allow creation of <see cref="ILspServices"/> since that can't always be handled in the constructor.
    /// </summary>
    /// <returns>An <see cref="ILspServices"/> instance for this server.</returns>
    /// <remarks>This should only be called once, and then cached.</remarks>
    protected abstract ILspServices ConstructLspServices();

    protected virtual AbstractHandlerProvider GetHandlerProvider()
    {
        var lspServices = _lspServices.Value;
        var handlerProvider = new HandlerProvider(lspServices);
        SetupRequestDispatcher(handlerProvider);

        return handlerProvider;
    }

    public ILspServices GetLspServices() => _lspServices.Value;

    protected virtual void SetupRequestDispatcher(AbstractHandlerProvider handlerProvider)
    {
        // Get unique set of methods from the handler provider for the default language.
        foreach (var methodGroup in handlerProvider
            .GetRegisteredMethods()
            .GroupBy(m => m.MethodName))
        {
            // Instead of concretely defining methods for each LSP method, we instead dynamically construct the
            // generic method info from the exported handler types.  This allows us to define multiple handlers for
            // the same method but different type parameters.  This is a key functionality to support LSP extensibility
            // in cases like XAML, TS to allow them to use different LSP type definitions

            // Verify that we are not mixing different numbers of request parameters and responses between different language handlers
            // e.g. it is not allowed to have a method have both a parameterless and regular parameter handler.
            var requestTypes = methodGroup.Select(m => m.RequestType);
            var responseTypes = methodGroup.Select(m => m.ResponseType);
            if (!(requestTypes.All(r => r is null) || requestTypes.Any(r => r is not null))
                || !(responseTypes.All(r => r is null) || responseTypes.Any(r => r is not null)))
            {
                throw new InvalidOperationException($"Language specific handlers for {methodGroup.Key} have mis-matched number of parameters or returns:{Environment.NewLine}{string.Join(Environment.NewLine, methodGroup)}");
            }

            // Pick the kind of streamjsonrpc handling based on the number of request / response arguments
            // We use the first since we've validated above that the language specific handlers have similarly shaped requests.
            var methodInfo = DelegatingEntryPoint.GetMethodInstantiation(methodGroup.First().RequestType, methodGroup.First().ResponseType);

            var delegatingEntryPoint = new DelegatingEntryPoint(methodGroup.Key, this);

            var methodAttribute = new JsonRpcMethodAttribute(methodGroup.Key)
            {
                UseSingleObjectParameterDeserialization = true,
            };

            _jsonRpc.AddLocalRpcMethod(methodInfo, delegatingEntryPoint, methodAttribute);
        }
    }

    [JsonRpcMethod("shutdown")]
    public Task HandleShutdownRequestAsync(CancellationToken _) => ShutdownAsync();

    [JsonRpcMethod("exit")]
    public Task HandleExitNotificationAsync(CancellationToken _) => ExitAsync();

    public virtual void OnInitialized()
    {
        IsInitialized = true;
    }

    protected virtual IRequestExecutionQueue<TRequestContext> ConstructRequestExecutionQueue()
    {
        var handlerProvider = GetHandlerProvider();
        var queue = new RequestExecutionQueue<TRequestContext>(this, _logger, handlerProvider);

        queue.Start();

        return queue;
    }

    protected IRequestExecutionQueue<TRequestContext> GetRequestExecutionQueue()
    {
        return _queue.Value;
    }

    private sealed class DelegatingEntryPoint
    {
        private readonly string _method;
        private readonly AbstractLanguageServer<TRequestContext> _target;

        private static readonly MethodInfo s_entryPointMethod = typeof(DelegatingEntryPoint).GetMethod(nameof(EntryPointAsync))!;
        private static readonly MethodInfo s_parameterlessEntryPointMethod = typeof(DelegatingEntryPoint).GetMethod(nameof(ParameterlessEntryPointAsync))!;
        private static readonly MethodInfo s_notificationMethod = typeof(DelegatingEntryPoint).GetMethod(nameof(NotificationEntryPointAsync))!;
        private static readonly MethodInfo s_parameterlessNotificationMethod = typeof(DelegatingEntryPoint).GetMethod(nameof(ParameterlessNotificationEntryPointAsync))!;

        public DelegatingEntryPoint(string method, AbstractLanguageServer<TRequestContext> target)
        {
            _method = method;
            _target = target;
        }

        public static MethodInfo GetMethodInstantiation(Type? requestType, Type? responseType)
            => (requestType, responseType) switch
            {
                (requestType: not null, responseType: not null) => s_entryPointMethod,
                (requestType: null, responseType: not null) => s_parameterlessEntryPointMethod,
                (requestType: not null, responseType: null) => s_notificationMethod,
                (requestType: null, responseType: null) => s_parameterlessNotificationMethod,
            };

        public Task NotificationEntryPointAsync(object request, CancellationToken cancellationToken) => ExecuteRequestAsync(request, cancellationToken);

        public Task ParameterlessNotificationEntryPointAsync(CancellationToken cancellationToken) => ExecuteRequestAsync(null, cancellationToken);

        public Task<object?> EntryPointAsync(object request, CancellationToken cancellationToken) => ExecuteRequestAsync(request, cancellationToken);

        public Task<object?> ParameterlessEntryPointAsync(CancellationToken cancellationToken) => ExecuteRequestAsync(null, cancellationToken);

        private Task<object?> ExecuteRequestAsync(object? request, CancellationToken cancellationToken)
        {
            var queue = _target.GetRequestExecutionQueue();
            var lspServices = _target.GetLspServices();

            return queue.ExecuteAsync(request, _method, lspServices, cancellationToken);
        }
    }

    public Task WaitForExitAsync()
    {
        lock (_lifeCycleLock)
        {
            // Ensure we've actually been asked to shutdown before waiting.
            if (_shutdownRequestTask == null)
            {
                throw new InvalidOperationException("The language server has not yet been asked to shutdown.");
            }
        }

        // Note - we return the _serverExitedSource task here instead of the _exitNotification task as we may not have
        // finished processing the exit notification before a client calls into us asking to restart.
        // This is because unlike shutdown, exit is a notification where clients do not need to wait for a response.
        return _serverExitedSource.Task;
    }

    /// <summary>
    /// Tells the LSP server to stop handling any more incoming messages (other than exit).
    /// Typically called from an LSP shutdown request.
    /// </summary>
    public Task ShutdownAsync(string message = "Shutting down")
    {
        Task shutdownTask;
        lock (_lifeCycleLock)
        {
            // Run shutdown or return the already running shutdown request.
            _shutdownRequestTask ??= Shutdown_NoLockAsync(message);
            shutdownTask = _shutdownRequestTask;
            return shutdownTask;
        }

        // Runs the actual shutdown outside of the lock - guaranteed to be only called once by the above code.
        async Task Shutdown_NoLockAsync(string message)
        {
            // Immediately yield so that this does not run under the lock.
            await Task.Yield();

            _logger.LogInformation(message);

            // Allow implementations to do any additional cleanup on shutdown.
            var lifeCycleManager = GetLspServices().GetRequiredService<ILifeCycleManager>();
            await lifeCycleManager.ShutdownAsync(message).ConfigureAwait(false);

            await ShutdownRequestExecutionQueueAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tells the LSP server to exit.  Requires that <see cref="ShutdownAsync(string)"/> was called first.
    /// Typically called from an LSP exit notification.
    /// </summary>
    public Task ExitAsync()
    {
        Task exitTask;
        lock (_lifeCycleLock)
        {
            if (_shutdownRequestTask?.IsCompleted != true)
            {
                throw new InvalidOperationException("The language server has not yet been asked to shutdown or has not finished shutting down.");
            }

            // Run exit or return the already running exit request.
            _exitNotificationTask ??= Exit_NoLockAsync();
            exitTask = _exitNotificationTask;
            return exitTask;
        }

        // Runs the actual exit outside of the lock - guaranteed to be only called once by the above code.
        async Task Exit_NoLockAsync()
        {
            // Immediately yield so that this does not run under the lock.
            await Task.Yield();

            try
            {
                var lspServices = GetLspServices();

                // Allow implementations to do any additional cleanup on exit.
                var lifeCycleManager = lspServices.GetRequiredService<ILifeCycleManager>();
                await lifeCycleManager.ExitAsync().ConfigureAwait(false);

                await ShutdownRequestExecutionQueueAsync().ConfigureAwait(false);

                lspServices.Dispose();

                _jsonRpc.Disconnected -= JsonRpc_Disconnected;
                _jsonRpc.Dispose();
            }
            catch (Exception)
            {
                // Swallow exceptions thrown by disposing our JsonRpc object. Disconnected events can potentially throw their own exceptions so
                // we purposefully ignore all of those exceptions in an effort to shutdown gracefully.
            }
            finally
            {
                _logger.LogInformation("Exiting server");
                _serverExitedSource.TrySetResult(null);
            }
        }
    }

    private ValueTask ShutdownRequestExecutionQueueAsync()
    {
        var queue = GetRequestExecutionQueue();
        return queue.DisposeAsync();
    }

#pragma warning disable VSTHRD100
    /// <summary>
    /// Cleanup the server if we encounter a json rpc disconnect so that we can be restarted later.
    /// </summary>
    private async void JsonRpc_Disconnected(object? sender, JsonRpcDisconnectedEventArgs e)
    {
        // It is possible this gets called during normal shutdown and exit.
        // ShutdownAsync and ExitAsync will no-op if shutdown was already triggered by something else.
        await ShutdownAsync(message: "Shutdown triggered by JsonRpc disconnect").ConfigureAwait(false);
        await ExitAsync().ConfigureAwait(false);
    }
#pragma warning disable VSTHRD100

    internal TestAccessor GetTestAccessor()
    {
        return new(this);
    }

    internal readonly struct TestAccessor
    {
        private readonly AbstractLanguageServer<TRequestContext> _server;

        internal TestAccessor(AbstractLanguageServer<TRequestContext> server)
        {
            _server = server;
        }

        public T GetRequiredLspService<T>() where T : class => _server.GetLspServices().GetRequiredService<T>();

        internal RequestExecutionQueue<TRequestContext>.TestAccessor? GetQueueAccessor()
        {
            if (_server._queue.Value is RequestExecutionQueue<TRequestContext> requestExecution)
                return requestExecution.GetTestAccessor();

            return null;
        }

        internal Task<object?> ExecuteRequestAsync(string methodName, object? request, CancellationToken cancellationToken)
        {
            return _server._queue.Value.ExecuteAsync(request, methodName, _server._lspServices.Value, cancellationToken);
        }

        internal JsonRpc GetServerRpc() => _server._jsonRpc;

        internal bool HasShutdownStarted()
        {
            lock (_server._lifeCycleLock)
            {
                return _server._shutdownRequestTask != null;
            }
        }
    }
}
