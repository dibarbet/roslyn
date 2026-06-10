// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Microsoft.CodeAnalysis.LanguageServer.Daemon;

namespace Microsoft.CodeAnalysis.LanguageServer.Client;

internal enum DaemonConnectStatus
{
    Connected,
    FallbackToNonDaemon,
}

internal sealed class DaemonConnectResult : IDisposable
{
    private DaemonConnectResult(DaemonConnectStatus status, Stream? stream)
    {
        Status = status;
        Stream = stream;
    }

    public DaemonConnectStatus Status { get; }
    public Stream? Stream { get; }

    public static DaemonConnectResult Connected(Stream stream)
        => new(DaemonConnectStatus.Connected, stream);

    public static DaemonConnectResult FallbackToNonDaemon()
        => new(DaemonConnectStatus.FallbackToNonDaemon, stream: null);

    public void Dispose()
        => Stream?.Dispose();
}

internal static class DaemonClient
{
    private const int ExistingDaemonMutexTimeoutMs = 5_000;
    private const int NewDaemonMutexTimeoutMs = 20_000;
    private const int ExistingDaemonConnectTimeoutMs = 5_000;
    private const int NewDaemonConnectTimeoutMs = 20_000;

    public static Task<DaemonConnectResult> ConnectAsync(
        ServerExecutable executable,
        IReadOnlyList<string> serverArguments,
        CancellationToken cancellationToken)
    {
        if (executable is null)
            throw new ArgumentNullException(nameof(executable));
        if (serverArguments is null)
            throw new ArgumentNullException(nameof(serverArguments));

        cancellationToken.ThrowIfCancellationRequested();

        var pipeName = DaemonPipeName.GetPipeName(executable.ToolIdentifier);
        var serverMutexName = DaemonPipeName.GetServerMutexName(pipeName);
        var clientMutexName = DaemonPipeName.GetClientMutexName(pipeName);
        var serverWasRunning = DaemonMutex.WasOpen(serverMutexName);
        var mutexTimeoutMs = serverWasRunning ? ExistingDaemonMutexTimeoutMs : NewDaemonMutexTimeoutMs;

        using var clientMutex = new DaemonMutex(clientMutexName, out _);
        if (!clientMutex.IsLocked && !clientMutex.TryLock(mutexTimeoutMs))
        {
            Console.Error.WriteLine($"Timed out waiting for daemon startup mutex '{clientMutexName}'. Falling back to non-daemon mode.");
            return Task.FromResult(DaemonConnectResult.FallbackToNonDaemon());
        }

        var launchedDaemon = false;
        serverWasRunning = DaemonMutex.WasOpen(serverMutexName);
        if (!serverWasRunning)
        {
            LaunchDaemon(executable, pipeName, serverArguments, cancellationToken);
            launchedDaemon = true;
        }

        var pipeClient = DaemonNamedPipe.CreateClientStream(pipeName);
        try
        {
            var connectTimeoutMs = launchedDaemon ? NewDaemonConnectTimeoutMs : ExistingDaemonConnectTimeoutMs;
            pipeClient.Connect(connectTimeoutMs);
            return Task.FromResult(DaemonConnectResult.Connected(pipeClient));
        }
        catch
        {
            pipeClient.Dispose();
            throw;
        }
    }

    private static void LaunchDaemon(
        ServerExecutable executable,
        string pipeName,
        IReadOnlyList<string> serverArguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var daemonArguments = new List<string>(serverArguments.Count + 3)
        {
            "--daemon",
            "--daemonPipeName",
            pipeName,
        };
        daemonArguments.AddRange(serverArguments);

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            CreateNoWindow = true,
        };
        executable.AddCommandPrefix(startInfo);
        foreach (var argument in daemonArguments)
            startInfo.ArgumentList.Add(argument);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the language server daemon process.");

        _ = ProcessUtilities.ForwardStandardErrorAsync(process, CancellationToken.None);
        Console.Error.WriteLine($"Started language server daemon: {ProcessUtilities.GetCommandLineForDisplay(executable, daemonArguments)}");
    }
}
