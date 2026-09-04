// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// Merges multiple connection sources into one stream. Completion of any source stops the aggregate,
/// so a daemon's primary named-pipe source can continue to control process lifetime.
/// </summary>
internal sealed class AggregateLanguageServerConnectionSource : ILanguageServerConnectionSource, IDisposable
{
    private readonly ILanguageServerConnectionSource[] _sources;

    public AggregateLanguageServerConnectionSource(params ILanguageServerConnectionSource[] sources)
    {
        if (sources.Length == 0)
            throw new ArgumentException("At least one connection source is required.", nameof(sources));

        var shouldIsolateConnectionFaults = sources[0].ShouldIsolateConnectionFaults;
        if (sources.Any(source => source.ShouldIsolateConnectionFaults != shouldIsolateConnectionFaults))
            throw new ArgumentException("All connection sources must use the same fault-isolation policy.", nameof(sources));

        _sources = sources;
        ShouldIsolateConnectionFaults = shouldIsolateConnectionFaults;
    }

    public bool ShouldIsolateConnectionFaults { get; }

    public async IAsyncEnumerable<LanguageServerConnection> AcceptConnectionsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var connections = Channel.CreateUnbounded<LanguageServerConnection>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        using var lifetimeSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completed = 0;
        var pumps = _sources.Select(PumpAsync).ToArray();

        try
        {
            await foreach (var connection in connections.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return connection;
        }
        finally
        {
            lifetimeSource.Cancel();
            await Task.WhenAll(pumps).ConfigureAwait(false);

            while (connections.Reader.TryRead(out var connection))
                connection.Resource?.Dispose();
        }

        async Task PumpAsync(ILanguageServerConnectionSource source)
        {
            Exception? error = null;

            try
            {
                await foreach (var connection in source.AcceptConnectionsAsync(lifetimeSource.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await connections.Writer.WriteAsync(connection, lifetimeSource.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        connection.Resource?.Dispose();
                        throw;
                    }
                }
            }
            catch (OperationCanceledException) when (lifetimeSource.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                if (Interlocked.Exchange(ref completed, 1) == 0)
                {
                    connections.Writer.TryComplete(error);
                    lifetimeSource.Cancel();
                }
            }
        }
    }

    public void Dispose()
    {
        foreach (var source in _sources)
            (source as IDisposable)?.Dispose();
    }
}
