// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// Queues in-process connections so they enter the same connection-manager loop as transport connections.
/// </summary>
internal sealed class InProcessLanguageServerConnectionSource : ILanguageServerConnectionSource, IDisposable
{
    private readonly object _gate = new();
    private readonly Channel<LanguageServerConnection> _connections =
        Channel.CreateUnbounded<LanguageServerConnection>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private bool _stopped;

    public bool ShouldIsolateConnectionFaults => true;

    /// <summary>
    /// Queues a connection and returns a task that completes after the connection manager finishes supervising
    /// its language server and disposes <paramref name="resource"/>.
    /// </summary>
    public Task EnqueueConnection(Stream inputStream, Stream outputStream, IDisposable resource)
    {
        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectionResource = new CompletionResource(resource, completionSource);

        lock (_gate)
        {
            if (_stopped ||
                !_connections.Writer.TryWrite(
                    new LanguageServerConnection(inputStream, outputStream, connectionResource)))
            {
                connectionResource.Dispose();
                throw new InvalidOperationException("The language server daemon is no longer accepting connections.");
            }
        }

        return completionSource.Task;
    }

    public async IAsyncEnumerable<LanguageServerConnection> AcceptConnectionsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var connection in _connections.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return connection;
        }
        finally
        {
            StopAndDrain();
        }
    }

    public void Dispose()
        => StopAndDrain();

    private void StopAndDrain()
    {
        lock (_gate)
        {
            if (_stopped)
                return;

            _stopped = true;
            _connections.Writer.TryComplete();
        }

        while (_connections.Reader.TryRead(out var connection))
            connection.Resource?.Dispose();
    }

    private sealed class CompletionResource(IDisposable resource, TaskCompletionSource completionSource) : IDisposable
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
                completionSource.TrySetResult();
            }
        }
    }
}
