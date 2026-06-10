// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;

namespace Microsoft.CodeAnalysis.LanguageServer.Client;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ThinClientArguments thinClientArguments;
        try
        {
            thinClientArguments = ThinClientArguments.Parse(args);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentNullException)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.BadArguments;
        }

        var monitoredClientExited = false;
        using var cancellationSource = new CancellationTokenSource();
        var monitorTask = StartClientProcessMonitorAsync(
            thinClientArguments.ClientProcessId,
            () =>
            {
                monitoredClientExited = true;
                cancellationSource.Cancel();
            },
            cancellationSource.Token);

        try
        {
            var executable = ServerExecutableResolver.Resolve();
            using var editorConnection = await EditorConnection.CreateAsync(thinClientArguments, cancellationSource.Token).ConfigureAwait(false);

            if (thinClientArguments.DaemonMode)
            {
                var daemonResult = await DaemonClient.ConnectAsync(
                    executable,
                    thinClientArguments.ServerArguments,
                    cancellationSource.Token).ConfigureAwait(false);

                if (daemonResult.Status == DaemonConnectStatus.Connected)
                {
                    using (daemonResult)
                    {
                        return await RelayDaemonAsync(
                            daemonResult.Stream!,
                            editorConnection,
                            () => monitoredClientExited,
                            cancellationSource.Token).ConfigureAwait(false);
                    }
                }

                Console.Error.WriteLine("Running language server in non-daemon fallback mode.");
            }

            return await ChildServerHost.RunAsync(
                executable,
                thinClientArguments.ServerArguments,
                editorConnection,
                () => monitoredClientExited,
                cancellationSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (monitoredClientExited)
        {
            Console.Error.WriteLine("Monitored editor process exited.");
            return ExitCodes.EditorConnectionLost;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or InvalidOperationException or TimeoutException)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.ServerLaunchOrConnectFailure;
        }
        finally
        {
            cancellationSource.Cancel();
            try
            {
                await monitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static async Task<int> RelayDaemonAsync(
        Stream daemonStream,
        EditorConnection editorConnection,
        Func<bool> hasMonitoredClientExited,
        CancellationToken cancellationToken)
    {
        var relayResult = await LspRelay.RelayAsync(
            editorConnection.Input,
            editorConnection.Output,
            daemonStream,
            daemonStream,
            cancellationToken).ConfigureAwait(false);

        if (hasMonitoredClientExited() || relayResult.ClosedEndpoint == RelayEndpoint.Editor)
        {
            Console.Error.WriteLine("Editor connection closed before the language server daemon connection.");
            return ExitCodes.EditorConnectionLost;
        }

        Console.Error.WriteLine("Language server daemon connection closed before the editor connection.");
        return ExitCodes.ServerConnectionLost;
    }

    private static Task StartClientProcessMonitorAsync(int? processId, Action onClientExited, CancellationToken cancellationToken)
    {
        if (onClientExited is null)
            throw new ArgumentNullException(nameof(onClientExited));

        if (processId is null)
            return Task.CompletedTask;

        return Task.Run(async () =>
        {
            Process? process = null;
            try
            {
                process = Process.GetProcessById(processId.Value);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                onClientExited();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (ArgumentException)
            {
                onClientExited();
            }
            catch (InvalidOperationException)
            {
                onClientExited();
            }
            finally
            {
                process?.Dispose();
            }
        });
    }
}
