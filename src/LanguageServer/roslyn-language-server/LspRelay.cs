// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO.Pipes;

namespace Microsoft.CodeAnalysis.LanguageServer.Client;

internal enum RelayEndpoint
{
    Editor,
    Server,
    Cancelled,
}

internal readonly struct RelayResult
{
    public RelayResult(RelayEndpoint closedEndpoint, Exception? exception)
    {
        ClosedEndpoint = closedEndpoint;
        Exception = exception;
    }

    public RelayEndpoint ClosedEndpoint { get; }
    public Exception? Exception { get; }
}

internal static class LspRelay
{
    private const int BufferSize = 64 * 1024;

    public static async Task<RelayResult> RelayAsync(
        Stream editorInput,
        Stream editorOutput,
        Stream serverInput,
        Stream serverOutput,
        CancellationToken cancellationToken)
    {
        if (editorInput is null)
            throw new ArgumentNullException(nameof(editorInput));
        if (editorOutput is null)
            throw new ArgumentNullException(nameof(editorOutput));
        if (serverInput is null)
            throw new ArgumentNullException(nameof(serverInput));
        if (serverOutput is null)
            throw new ArgumentNullException(nameof(serverOutput));

        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var editorToServer = CopyUntilClosedAsync(editorInput, serverOutput, RelayEndpoint.Editor, RelayEndpoint.Server, cancellationSource.Token);
        var serverToEditor = CopyUntilClosedAsync(serverInput, editorOutput, RelayEndpoint.Server, RelayEndpoint.Editor, cancellationSource.Token);
        var completedTask = await Task.WhenAny(editorToServer, serverToEditor).ConfigureAwait(false);

        cancellationSource.Cancel();
        return await completedTask.ConfigureAwait(false);
    }

    private static async Task<RelayResult> CopyUntilClosedAsync(
        Stream input,
        Stream output,
        RelayEndpoint inputEndpoint,
        RelayEndpoint outputEndpoint,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];

        try
        {
            while (true)
            {
                int bytesRead;
                try
                {
                    bytesRead = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    return new RelayResult(inputEndpoint, ex);
                }
                catch (ObjectDisposedException ex)
                {
                    return new RelayResult(inputEndpoint, ex);
                }
                catch (NotSupportedException ex) when (input is PipeStream)
                {
                    return new RelayResult(inputEndpoint, ex);
                }

                if (bytesRead == 0)
                    return new RelayResult(inputEndpoint, null);

                try
                {
                    await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    return new RelayResult(outputEndpoint, ex);
                }
                catch (ObjectDisposedException ex)
                {
                    return new RelayResult(outputEndpoint, ex);
                }
                catch (NotSupportedException ex) when (output is PipeStream)
                {
                    return new RelayResult(outputEndpoint, ex);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RelayResult(RelayEndpoint.Cancelled, null);
        }
    }
}
