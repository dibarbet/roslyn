// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis.LanguageServer.LanguageServer;
using Microsoft.CommonLanguageServerProtocol.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Composition;

namespace Microsoft.CodeAnalysis.LanguageServer;

internal sealed class LanguageServerConnectionManager
{
    private readonly object _gate = new();
    private ImmutableArray<ServerEntry> _servers = [];
    private readonly List<Task> _supervisors = [];

    // Test hook: invoked just before LanguageServerHost.Start(). Throw to simulate a startup failure.
    private Action? _onBeforeStartServer;

    /// <summary>
    /// Runs an independent language server for each connection yielded by <paramref name="connectionSource"/>.
    /// A <see cref="SingleLanguageServerConnectionSource"/> yields exactly one connection and then completes, so
    /// this returns once that server exits. The daemon listener yields connections until its internally managed idle
    /// timeout elapses or <paramref name="cancellationToken"/> is signaled.
    /// </summary>
    public async Task RunAsync(
        ILanguageServerConnectionSource connectionSource,
        ExportProvider exportProvider,
        AbstractTypeRefResolver typeRefResolver,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // For a source that isolates faults (the daemon), a server fault is logged and confined to that one
        // connection; otherwise a single misbehaving client would tear down the whole daemon.
        var isolateFaults = connectionSource.ShouldIsolateConnectionFaults;

        try
        {
            await foreach (var connection in connectionSource.AcceptConnectionsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (isolateFaults)
                {
                    // Daemon mode: start server construction and supervision in a background task so this
                    // accept loop can immediately loop back to WaitForConnectionAsync for the next client,
                    // without waiting for (potentially slow) MEF composition to finish.
                    var supervisor = Task.Run(
                        () => StartAndSuperviseAsync(connection, exportProvider, typeRefResolver, logger, isolateFaults, cancellationToken),
                        CancellationToken.None);
                    TrackSupervisor(supervisor, removeWhenCompleted: true);
                }
                else
                {
                    // Single-server mode starts synchronously until it begins waiting for server exit, so no
                    // Task.Run or parallel startup is needed.
                    TrackSupervisor(
                        StartAndSuperviseAsync(connection, exportProvider, typeRefResolver, logger, isolateFaults, cancellationToken),
                        removeWhenCompleted: false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // External process shutdown is expected; swallow and proceed.
        }

        if (cancellationToken.IsCancellationRequested && isolateFaults)
        {
            ImmutableArray<IDisposable> connections;
            lock (_gate)
                connections = _servers.SelectAsArray(
                    static entry => entry.Connection is not null,
                    static entry => entry.Connection!);

            foreach (var connection in connections)
                connection.Dispose();
        }

        // Drain all daemon supervisors even on external shutdown so no per-server task can outlive the shared
        // export provider and logger factory owned by Program. Single-server mode preserves the prior behavior
        // of returning promptly on external cancellation when its transport is not manager-owned.
        if (!cancellationToken.IsCancellationRequested || isolateFaults)
        {
            Task[] remainingSupervisors;
            lock (_gate)
                remainingSupervisors = [.. _supervisors];

            await Task.WhenAll(remainingSupervisors).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts and supervises a language server for a connection created outside the connection source.
    /// </summary>
    public async Task<LanguageServerHost> StartConnectionAsync(
        LanguageServerConnection connection,
        ExportProvider exportProvider,
        AbstractTypeRefResolver typeRefResolver,
        ILogger logger,
        bool isolateFaults,
        CancellationToken cancellationToken)
    {
        var entry = await TryStartServerAsync(
            connection, exportProvider, typeRefResolver, logger, cancellationToken).ConfigureAwait(false);
        if (entry is null)
            throw new OperationCanceledException(cancellationToken);

        var supervisor = SuperviseAsync(entry, logger, isolateFaults);
        TrackSupervisor(supervisor, removeWhenCompleted: isolateFaults);
        return entry.Server;
    }

    private async Task StartAndSuperviseAsync(
        LanguageServerConnection connection,
        ExportProvider exportProvider,
        AbstractTypeRefResolver typeRefResolver,
        ILogger logger,
        bool isolateFaults,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = await TryStartServerAsync(
                connection, exportProvider, typeRefResolver, logger, cancellationToken).ConfigureAwait(false);
            if (entry is not null)
                await SuperviseAsync(entry, logger, isolateFaults).ConfigureAwait(false);
        }
        catch (Exception ex) when (isolateFaults)
        {
            logger.LogError(ex, "Language server connection supervisor faulted.");
        }
    }

    // Creates, registers, and starts a language server for the connection. Returns null if shutdown won the
    // race with startup; construction and startup failures are cleaned up and propagated to the caller.
    private async Task<ServerEntry?> TryStartServerAsync(
        LanguageServerConnection connection,
        ExportProvider exportProvider,
        AbstractTypeRefResolver typeRefResolver,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // --- Phase 1: construct the LanguageServerHost (MEF composition happens here) ---
        LanguageServerHost server;
        try
        {
            server = new LanguageServerHost(connection.InputStream, connection.OutputStream, exportProvider, typeRefResolver);
        }
        catch
        {
            connection.Resource?.Dispose();
            throw;
        }

        var entry = new ServerEntry(server, connection.Resource);
        var abortStartup = false;

        // --- Phase 2: register and start ---
        // Register before starting so GetStartedServers reflects the server before its JSON-RPC listen loop
        // is active.
        lock (_gate)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _servers = _servers.Add(entry);
            }
            else
            {
                abortStartup = true;
            }
        }

        if (abortStartup)
        {
            await AbortServerAsync(server, logger).ConfigureAwait(false);
            connection.Resource?.Dispose();
            return null;
        }

        try
        {
            _onBeforeStartServer?.Invoke();
            server.Start();
        }
        catch
        {
            lock (_gate)
                _servers = _servers.Remove(entry);

            await AbortServerAsync(server, logger).ConfigureAwait(false);
            connection.Resource?.Dispose();
            throw;
        }

        return entry;
    }

    private static async Task AbortServerAsync(LanguageServerHost server, ILogger logger)
    {
        try
        {
            await server.AbortAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to clean up a language server after startup was aborted.");
        }
    }

    // Awaits a server's exit, then unregisters it and disposes its connection.
    private async Task SuperviseAsync(ServerEntry entry, ILogger logger, bool isolateFaults)
    {
        try
        {
            // Wait until the server exits. We specifically do not also wait on the JsonRpc completion; the
            // server exiting (via an explicit 'exit' or an observed disconnect) is the only signal we need.
            await entry.Server.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (isolateFaults)
        {
            logger.LogError(ex, "Language server connection faulted; tearing down that connection.");
        }
        finally
        {
            lock (_gate)
                _servers = _servers.Remove(entry);

            // Dispose this connection's transport now that its server has fully exited.
            entry.Connection?.Dispose();
        }
    }

    private void TrackSupervisor(Task supervisor, bool removeWhenCompleted)
    {
        lock (_gate)
            _supervisors.Add(supervisor);

        if (removeWhenCompleted)
        {
            _ = supervisor.ContinueWith(
                RemoveCompletedSupervisor,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        void RemoveCompletedSupervisor(Task completedSupervisor)
        {
            lock (_gate)
                _supervisors.Remove(completedSupervisor);
        }
    }

    public ImmutableArray<LanguageServerHost> GetStartedServers()
    {
        lock (_gate)
        {
            return _servers.SelectAsArray(entry => entry.Server.HasStarted, entry => entry.Server);
        }
    }

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor
    {
        private readonly LanguageServerConnectionManager _instance;

        internal TestAccessor(LanguageServerConnectionManager instance) => _instance = instance;

        /// <summary>
        /// When set, invoked just before each <see cref="LanguageServerHost.Start"/> call. Throw from
        /// this delegate to simulate a startup failure (for daemon-mode fault-isolation tests).
        /// </summary>
        internal Action? OnBeforeStartServer
        {
            set => _instance._onBeforeStartServer = value;
        }
    }

    private sealed class ServerEntry(LanguageServerHost server, IDisposable? connection)
    {
        public LanguageServerHost Server { get; } = server;
        public IDisposable? Connection { get; } = connection;
    }
}
