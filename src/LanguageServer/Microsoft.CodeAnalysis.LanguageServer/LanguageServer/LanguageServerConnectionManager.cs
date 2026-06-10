// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.LanguageServer.Daemon;
using Microsoft.CodeAnalysis.LanguageServer.LanguageServer;
using Microsoft.CommonLanguageServerProtocol.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Composition;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.LanguageServer;

internal sealed class LanguageServerConnectionManager
{
    private readonly object _gate = new();
    private ImmutableArray<ServerEntry> _servers = [];

    /// <summary>
    /// Daemon-only state. Set once when <see cref="RunDaemonAsync"/> starts and only meaningful while
    /// running as a daemon. All access is guarded by <see cref="_gate"/>.
    /// </summary>
    private bool _isDaemon;
    private int _pendingAccepts;
    private TimeSpan _keepAlive;
    private CancellationTokenSource? _daemonShutdownCts;
    private CancellationTokenSource? _keepAliveCts;
    private ILogger? _logger;

    public LanguageServerHost CreateLanguageServerHost(
        Stream inputStream,
        Stream outputStream,
        ExportProvider exportProvider,
        AbstractTypeRefResolver typeRefResolver,
        IDisposable? connection = null)
    {
        var entry = new ServerEntry { Connection = connection };

        lock (_gate)
        {
            _servers = _servers.Add(entry);
        }

        try
        {
            var server = new LanguageServerHost(inputStream, outputStream, exportProvider, typeRefResolver);
            entry.Server = server;

            server.Start();

            _ = TrackServerExitAsync(entry);
            return server;
        }
        catch (Exception ex)
        {
            Unregister(entry);
            entry.Exited.TrySetException(ex);
            throw;
        }
    }

    /// <summary>
    /// Runs the multi-client daemon: holds the server mutex for the daemon's lifetime, accepts client
    /// connections on the given named pipe (each getting its own independent language server instance),
    /// and shuts down after the configured keepalive elapses with no connected clients.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if this process ran as the daemon; <see langword="false"/> if another
    /// daemon already owns <paramref name="pipeName"/> and this process should exit.
    /// </returns>
    public async Task<bool> RunDaemonAsync(
        string pipeName,
        TimeSpan keepAlive,
        ExportProvider exportProvider,
        AbstractTypeRefResolver typeRefResolver,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Hold the server mutex for the daemon's lifetime. We deliberately do NOT lock it
        // (initiallyOwned: false) because clients only check for the mutex's existence via
        // Mutex.TryOpenExisting, which is satisfied as long as we keep an open handle. Avoiding the lock
        // also avoids Mutex's thread-affinity requirement on release, which is incompatible with this
        // async method resuming on arbitrary threads (Dispose merely closes the handle and is thread-safe).
        var serverMutexName = DaemonPipeName.GetServerMutexName(pipeName);
        using var serverMutex = new Mutex(initiallyOwned: false, serverMutexName, out var createdNew);
        if (!createdNew)
        {
            logger.LogError("A language server daemon is already running on pipe '{pipeName}'.", pipeName);
            return false;
        }

        CancellationTokenSource shutdownCts;
        lock (_gate)
        {
            _isDaemon = true;
            _keepAlive = keepAlive;
            _logger = logger;
            _daemonShutdownCts = shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // An idle daemon that never receives a client should still time out and exit.
            MaybeStartKeepAlive_NoLock();
        }

        var shutdownToken = shutdownCts.Token;
        logger.LogInformation("Language server daemon listening on pipe '{pipeName}' (keepalive: {keepAlive}).", pipeName, keepAlive);

        try
        {
            while (!shutdownToken.IsCancellationRequested)
            {
                var pipeStream = DaemonNamedPipe.CreateServerStream(pipeName);
                try
                {
                    await pipeStream.WaitForConnectionAsync(shutdownToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await pipeStream.DisposeAsync().ConfigureAwait(false);
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Daemon encountered an error while waiting for a client connection.");
                    await pipeStream.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                lock (_gate)
                {
                    // Count the accepted-but-not-yet-registered connection so a concurrently firing
                    // keepalive timer doesn't shut the daemon down out from under it.
                    _pendingAccepts++;
                    CancelKeepAlive_NoLock();
                }

                logger.LogInformation("Daemon accepted a new client connection.");
                try
                {
                    // Each accepted stream becomes its own fully independent language server instance.
                    // The stream is disposed when that instance tears down (see Unregister).
                    CreateLanguageServerHost(pipeStream, pipeStream, exportProvider, typeRefResolver, connection: pipeStream);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Daemon failed to start a language server for the accepted connection.");
                    await pipeStream.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate)
                    {
                        _pendingAccepts--;
                        MaybeStartKeepAlive_NoLock();
                    }
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                CancelKeepAlive_NoLock();
                _daemonShutdownCts = null;
            }

            shutdownCts.Dispose();
        }

        logger.LogInformation("Language server daemon on pipe '{pipeName}' is shutting down.", pipeName);
        return true;
    }

    public ImmutableArray<LanguageServerHost> GetStartedServers()
    {
        lock (_gate)
        {
            return _servers.Where(entry => entry.Server is { HasStarted: true }).SelectAsArray(entry => entry.Server!);
        }
    }

    public async Task WaitForExitAsync()
    {
        while (true)
        {
            Task exitTask;

            lock (_gate)
            {
                if (_servers.IsEmpty)
                    return;

                exitTask = _servers[0].Exited.Task;
            }

            await exitTask.ConfigureAwait(false);
        }
    }

    private async Task TrackServerExitAsync(ServerEntry entry)
    {
        Contract.ThrowIfNull(entry.Server);

        try
        {
            await entry.Server.WaitForExitAsync().ConfigureAwait(false);
            Unregister(entry);
            entry.Exited.TrySetResult();
        }
        catch (Exception ex)
        {
            Unregister(entry);
            entry.Exited.TrySetException(ex);
        }
    }

    private void Unregister(ServerEntry entry)
    {
        lock (_gate)
        {
            _servers = _servers.Remove(entry);

            // If this was the last client in daemon mode, start the keepalive countdown.
            MaybeStartKeepAlive_NoLock();
        }

        // Dispose the per-client connection (e.g. the daemon's NamedPipeServerStream) now that the
        // server instance has fully exited. Disposal is idempotent, so it's safe even if the underlying
        // JsonRpc already closed the stream.
        entry.Connection?.Dispose();
    }

    /// <summary>
    /// Cancels any in-progress keepalive countdown. Must be called under <see cref="_gate"/>.
    /// </summary>
    private void CancelKeepAlive_NoLock()
    {
        if (_keepAliveCts is not null)
        {
            _keepAliveCts.Cancel();
            _keepAliveCts.Dispose();
            _keepAliveCts = null;
        }
    }

    /// <summary>
    /// In daemon mode, starts the keepalive countdown if there are no connected or pending clients and a
    /// finite keepalive is configured. When the countdown elapses (and the daemon is still idle), the
    /// daemon shutdown is triggered. Must be called under <see cref="_gate"/>.
    /// </summary>
    private void MaybeStartKeepAlive_NoLock()
    {
        if (!_isDaemon)
            return;

        // A client is connected or being accepted; no countdown.
        if (!_servers.IsEmpty || _pendingAccepts > 0)
            return;

        // Stay alive indefinitely.
        if (_keepAlive == Timeout.InfiniteTimeSpan)
            return;

        // Already counting down.
        if (_keepAliveCts is not null)
            return;

        // Already shutting down.
        if (_daemonShutdownCts is null || _daemonShutdownCts.IsCancellationRequested)
            return;

        var cts = _keepAliveCts = new CancellationTokenSource();
        var token = cts.Token;
        var keepAlive = _keepAlive;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(keepAlive, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A new client connected before the keepalive elapsed.
                return;
            }

            lock (_gate)
            {
                // Re-validate under the lock: only shut down if the daemon is still idle.
                if (!_servers.IsEmpty || _pendingAccepts > 0)
                    return;

                _logger?.LogInformation("Daemon keepalive elapsed with no active clients; shutting down.");
                _daemonShutdownCts?.Cancel();
            }
        });
    }

    private sealed class ServerEntry
    {
        public LanguageServerHost? Server;
        public IDisposable? Connection;
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}