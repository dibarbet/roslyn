// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;

namespace Microsoft.CodeAnalysis.LanguageServer.Client;

internal static class ChildServerHost
{
    public static async Task<int> RunAsync(
        ServerExecutable executable,
        IReadOnlyList<string> serverArguments,
        EditorConnection editorConnection,
        Func<bool> hasMonitoredClientExited,
        CancellationToken cancellationToken)
    {
        if (executable is null)
            throw new ArgumentNullException(nameof(executable));
        if (serverArguments is null)
            throw new ArgumentNullException(nameof(serverArguments));
        if (editorConnection is null)
            throw new ArgumentNullException(nameof(editorConnection));
        if (hasMonitoredClientExited is null)
            throw new ArgumentNullException(nameof(hasMonitoredClientExited));

        var childArguments = new List<string>(serverArguments.Count + 1)
        {
            "--stdio",
        };
        childArguments.AddRange(serverArguments);

        using var process = StartChildProcess(executable, childArguments);
        using var stderrCancellationSource = new CancellationTokenSource();
        _ = ProcessUtilities.ForwardStandardErrorAsync(process, stderrCancellationSource.Token);

        Console.Error.WriteLine($"Started language server child: {ProcessUtilities.GetCommandLineForDisplay(executable, childArguments)}");

        var relayTask = LspRelay.RelayAsync(
            editorConnection.Input,
            editorConnection.Output,
            process.StandardOutput.BaseStream,
            process.StandardInput.BaseStream,
            cancellationToken);

        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        var completedTask = await Task.WhenAny(relayTask, exitTask).ConfigureAwait(false);

        var relayResult = completedTask == relayTask ? await relayTask.ConfigureAwait(false) : (RelayResult?)null;

        // Ensure the child has fully exited before we inspect its exit code.
        if (!process.HasExited)
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (InvalidOperationException)
            {
            }
            catch (IOException)
            {
            }

            await ProcessUtilities.WaitForExitOrKillAsync(process, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        stderrCancellationSource.Cancel();

        // A clean child exit (code 0) means the session shut down gracefully (e.g. the editor sent `exit`,
        // or the server shut itself down). Surface success so the editor doesn't treat it as a crash.
        if (process.HasExited && process.ExitCode == ExitCodes.Success)
        {
            Console.Error.WriteLine("Language server child exited cleanly.");
            return ExitCodes.Success;
        }

        if (hasMonitoredClientExited() || relayResult?.ClosedEndpoint == RelayEndpoint.Editor)
        {
            Console.Error.WriteLine("Editor connection closed before the language server child.");
            return ExitCodes.EditorConnectionLost;
        }

        if (process.HasExited && process.ExitCode != ExitCodes.Success)
        {
            Console.Error.WriteLine($"Language server child exited with code {process.ExitCode}.");
            return process.ExitCode;
        }

        Console.Error.WriteLine("Language server child connection closed before the editor connection.");
        return ExitCodes.ServerConnectionLost;
    }

    private static Process StartChildProcess(ServerExecutable executable, IReadOnlyList<string> childArguments)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        executable.AddCommandPrefix(startInfo);
        foreach (var argument in childArguments)
            startInfo.ArgumentList.Add(argument);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the language server child process.");
    }
}
