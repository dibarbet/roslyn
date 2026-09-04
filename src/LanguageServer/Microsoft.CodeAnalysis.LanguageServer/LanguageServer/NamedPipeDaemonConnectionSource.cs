// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

extern alias MSBuildWorkspaces;

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.CodeAnalysis.LanguageServer.Daemon;
using Microsoft.Extensions.Logging;
using RoslynLog = Microsoft.CodeAnalysis.Internal.Log;

// Reuse the compiler server's named-pipe helper (Asynchronous | WriteThrough | CurrentUserOnly,
// MaxAllowedServerInstances, and Unix /tmp socket-path handling). It is source-linked into
// Microsoft.CodeAnalysis.Workspaces.MSBuild, which this project already references under the
// MSBuildWorkspaces alias, so we use that already-compiled copy rather than source-linking another
// copy into this assembly (which would collide with the MSBuild build host's copy of the same type).
using NamedPipeUtil = MSBuildWorkspaces::Microsoft.CodeAnalysis.NamedPipeUtil;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// A connection source for daemon mode: owns the server mutex (which signals "a daemon is running" for
/// this pipe) and accepts client connections on a named pipe, handing each a dedicated, independent
/// <see cref="System.IO.Pipes.NamedPipeServerStream"/>.
/// </summary>
internal sealed class NamedPipeDaemonConnectionSource : ILanguageServerConnectionSource, IDisposable
{
    private static readonly TimeSpan s_initialConnectionTimeout = TimeSpan.FromMinutes(1);

    private readonly string _pipeName;
    private readonly ILogger _logger;
    private readonly Mutex _serverMutex;
    private readonly ConnectionIdleTimeout _idleTimeout;
    private readonly CancellationTokenSource _shutdownSource = new();
    private readonly object _timeoutGate = new();

    private CancellationTokenSource _timeoutGenerationChangedSource = new();
    private int _timeoutGeneration;
    private bool _stopped;

    private Action? _onConnectionAccepted;

    private NamedPipeDaemonConnectionSource(
        string pipeName,
        Mutex serverMutex,
        TimeSpan initialConnectionTimeout,
        TimeSpan keepAlive,
        ILogger logger)
    {
        _pipeName = pipeName;
        _serverMutex = serverMutex;
        _idleTimeout = new ConnectionIdleTimeout(initialConnectionTimeout, keepAlive, logger);
        _logger = logger;
    }

    public bool ShouldIsolateConnectionFaults => true;

    /// <summary>
    /// Attempts to become the daemon for <paramref name="pipeName"/> by acquiring the server mutex.
    /// Returns <see langword="false"/> (without creating a source) if another daemon already owns it.
    /// </summary>
    public static bool TryCreate(
        string pipeName,
        TimeSpan keepAlive,
        ILogger logger,
        [NotNullWhen(true)] out NamedPipeDaemonConnectionSource? source,
        TimeSpan? initialConnectionTimeout = null)
    {
        if (!DaemonServerMutex.TryAcquire(pipeName, out var serverMutex))
        {
            logger.LogWarning(
                "A language server daemon already owns pipe '{pipeName}'; this instance will exit so clients use the existing daemon.",
                pipeName);
            source = null;
            return false;
        }

        source = new NamedPipeDaemonConnectionSource(
            pipeName, serverMutex, initialConnectionTimeout ?? s_initialConnectionTimeout, keepAlive, logger);
        RoslynLog.Logger.Log(RoslynLog.FunctionId.VSCode_LanguageServer_Daemon_Started, logLevel: RoslynLog.LogLevel.Information);
        return true;
    }

    public async IAsyncEnumerable<LanguageServerConnection> AcceptConnectionsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (timeoutToken, timeoutGeneration, timeoutGenerationChangedToken) = GetTimeoutState();
            var pipeStream = NamedPipeUtil.CreateServer(_pipeName);

            // Wait for a client (outside any 'yield return', which C# disallows inside a try/catch). On success
            // the stream's ownership passes to the yielded connection; on failure we dispose it here.
            try
            {
                using var acceptCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, timeoutToken, timeoutGenerationChangedToken);
                await pipeStream.WaitForConnectionAsync(acceptCancellationSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await pipeStream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (timeoutToken.IsCancellationRequested)
            {
                await pipeStream.DisposeAsync().ConfigureAwait(false);
                if (TryCommitTimeout(timeoutGeneration))
                {
                    _shutdownSource.Cancel();
                    yield break;
                }

                continue;
            }
            catch (OperationCanceledException) when (timeoutGenerationChangedToken.IsCancellationRequested)
            {
                await pipeStream.DisposeAsync().ConfigureAwait(false);
                continue;
            }
            catch (Exception ex)
            {
                // Failing to accept one connection shouldn't take down the daemon; log and try again.
                _logger.LogError(ex, "Daemon encountered an error while waiting for a client connection.");
                await pipeStream.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            _onConnectionAccepted?.Invoke();
            if (!TryOpenConnection())
            {
                await pipeStream.DisposeAsync().ConfigureAwait(false);
                yield break;
            }

            _logger.LogInformation("Daemon accepted a new client connection.");
            RoslynLog.Logger.Log(RoslynLog.FunctionId.VSCode_LanguageServer_Daemon_Client_Connected, logLevel: RoslynLog.LogLevel.Information);

            // The accepted stream is both input and output, and is disposed when its language server exits.
            yield return new LanguageServerConnection(pipeStream, pipeStream, new ConnectionResource(pipeStream, this));
        }
    }

    public async Task RunCliConnectionsAsync(
        Func<Stream, CancellationToken, Task> connectionHandler,
        CancellationToken cancellationToken)
    {
        var gate = new object();
        var supervisors = new List<Task>();

        while (true)
        {
            var (timeoutToken, timeoutGeneration, timeoutGenerationChangedToken) = GetTimeoutState();
            var pipeStream = NamedPipeUtil.CreateServer(DaemonPipeName.GetCliPipeName(_pipeName));

            try
            {
                using var acceptCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, _shutdownSource.Token, timeoutToken, timeoutGenerationChangedToken);
                await pipeStream.WaitForConnectionAsync(acceptCancellationSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _shutdownSource.IsCancellationRequested)
            {
                await pipeStream.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (timeoutToken.IsCancellationRequested)
            {
                await pipeStream.DisposeAsync().ConfigureAwait(false);
                if (TryCommitTimeout(timeoutGeneration))
                {
                    _shutdownSource.Cancel();
                    break;
                }

                continue;
            }
            catch (OperationCanceledException) when (timeoutGenerationChangedToken.IsCancellationRequested)
            {
                await pipeStream.DisposeAsync().ConfigureAwait(false);
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Daemon encountered an error while waiting for a CLI connection.");
                await pipeStream.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            if (!TryOpenConnection())
            {
                await pipeStream.DisposeAsync().ConfigureAwait(false);
                break;
            }

            _logger.LogInformation("Daemon accepted a new CLI connection.");

            var supervisor = Task.Run(async () =>
            {
                using var connection = new ConnectionResource(pipeStream, this);
                try
                {
                    await connectionHandler(pipeStream, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "CLI connection handler faulted.");
                }
            }, CancellationToken.None);

            lock (gate)
                supervisors.Add(supervisor);

            _ = supervisor.ContinueWith(
                completedTask =>
                {
                    lock (gate)
                        supervisors.Remove(completedTask);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        Task[] remainingSupervisors;
        lock (gate)
            remainingSupervisors = [.. supervisors];

        await Task.WhenAll(remainingSupervisors).ConfigureAwait(false);
    }

    public IDisposable CreateConnectionResource(IDisposable resource)
    {
        if (!TryOpenConnection())
        {
            resource.Dispose();
            throw new InvalidOperationException("The language server daemon is shutting down.");
        }

        return new ConnectionResource(resource, this);
    }

    private (CancellationToken TimeoutToken, int Generation, CancellationToken GenerationChangedToken) GetTimeoutState()
    {
        lock (_timeoutGate)
            return (_idleTimeout.TimeoutToken, _timeoutGeneration, _timeoutGenerationChangedSource.Token);
    }

    private bool TryOpenConnection()
    {
        CancellationTokenSource previousGenerationChangedSource;
        lock (_timeoutGate)
        {
            if (_stopped)
                return false;

            _idleTimeout.OpenConnection();
            _timeoutGeneration++;
            previousGenerationChangedSource = _timeoutGenerationChangedSource;
            _timeoutGenerationChangedSource = new CancellationTokenSource();
        }

        previousGenerationChangedSource.Cancel();
        previousGenerationChangedSource.Dispose();
        return true;
    }

    private bool TryCommitTimeout(int observedGeneration)
    {
        lock (_timeoutGate)
        {
            if (_stopped)
                return true;

            if (_timeoutGeneration != observedGeneration)
                return false;

            _stopped = true;
            _idleTimeout.CommitTimeout();
            return true;
        }
    }

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor
    {
        private readonly NamedPipeDaemonConnectionSource _instance;

        internal TestAccessor(NamedPipeDaemonConnectionSource instance) => _instance = instance;

        internal bool HasTimedOut => _instance._idleTimeout.TimeoutToken.IsCancellationRequested;

        internal void TriggerTimeout() => _instance._idleTimeout.GetTestAccessor().TriggerTimeout();

        internal Action? OnConnectionAccepted
        {
            set => _instance._onConnectionAccepted = value;
        }
    }

    private sealed class ConnectionResource(IDisposable resource, NamedPipeDaemonConnectionSource source) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                resource.Dispose();
            }
            finally
            {
                source._idleTimeout.CloseConnection();
                RoslynLog.Logger.Log(RoslynLog.FunctionId.VSCode_LanguageServer_Daemon_Client_Disconnected, logLevel: RoslynLog.LogLevel.Information);
            }
        }
    }

    public void Dispose()
    {
        _shutdownSource.Cancel();
        _timeoutGenerationChangedSource.Cancel();
        _timeoutGenerationChangedSource.Dispose();
        _shutdownSource.Dispose();
        _idleTimeout.Dispose();
        _serverMutex.Dispose();
    }
}
