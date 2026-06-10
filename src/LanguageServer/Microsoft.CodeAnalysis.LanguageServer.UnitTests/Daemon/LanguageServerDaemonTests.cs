// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.CodeAnalysis.LanguageServer.Daemon;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Composition;
using Roslyn.LanguageServer.Protocol;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests;

public sealed class LanguageServerDaemonTests : AbstractLanguageServerHostTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(60);

    public LanguageServerDaemonTests(ITestOutputHelper testOutputHelper) : base(testOutputHelper)
    {
    }

    [Fact]
    public async Task Daemon_IdleKeepAlive_ShutsDownOnItsOwn()
    {
        var pipeName = CreateUniquePipeName();
        var (exportProvider, typeRefResolver) = await CreateCompositionAsync();
        var manager = new LanguageServerConnectionManager();
        using var cts = new CancellationTokenSource();

        // With no clients, the daemon should shut itself down once the (short) keepalive elapses.
        var daemonTask = manager.RunDaemonAsync(pipeName, TimeSpan.FromSeconds(1), exportProvider, typeRefResolver, CreateLogger(), cts.Token);

        var completed = await Task.WhenAny(daemonTask, Task.Delay(s_timeout));
        Assert.Same(daemonTask, completed);
        Assert.True(await daemonTask);
        Assert.False(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task Daemon_SecondInstanceOnSamePipe_ReturnsFalse()
    {
        var pipeName = CreateUniquePipeName();
        var (exportProvider, typeRefResolver) = await CreateCompositionAsync();
        var manager = new LanguageServerConnectionManager();
        using var cts = new CancellationTokenSource();

        var daemonTask = Task.Run(() => manager.RunDaemonAsync(pipeName, Timeout.InfiniteTimeSpan, exportProvider, typeRefResolver, CreateLogger("Daemon1"), cts.Token));

        try
        {
            // Wait until the first daemon holds the server mutex.
            var serverMutexName = DaemonPipeName.GetServerMutexName(pipeName);
            await WaitForConditionAsync(() => DaemonMutex.WasOpen(serverMutexName));

            // A second daemon on the same pipe must observe the existing one and bail out.
            var secondManager = new LanguageServerConnectionManager();
            var ranAsDaemon = await secondManager.RunDaemonAsync(pipeName, Timeout.InfiniteTimeSpan, exportProvider, typeRefResolver, CreateLogger("Daemon2"), cts.Token);
            Assert.False(ranAsDaemon);
        }
        finally
        {
            cts.Cancel();
            Assert.True(await daemonTask);
        }
    }

    [Fact]
    public async Task Daemon_AcceptsClientAndInitializes()
    {
        var pipeName = CreateUniquePipeName();
        var (exportProvider, typeRefResolver) = await CreateCompositionAsync();
        var manager = new LanguageServerConnectionManager();
        using var cts = new CancellationTokenSource();

        var daemonTask = Task.Run(() => manager.RunDaemonAsync(pipeName, Timeout.InfiniteTimeSpan, exportProvider, typeRefResolver, CreateLogger(), cts.Token));

        try
        {
            await using var client = await ConnectClientAsync(pipeName, cts.Token);
            var result = await client.Rpc.InvokeWithParameterObjectAsync<InitializeResult>(
                Methods.InitializeName, new InitializeParams { Capabilities = new ClientCapabilities() }, cts.Token);
            Assert.NotNull(result.Capabilities);
        }
        finally
        {
            cts.Cancel();
            Assert.True(await daemonTask);
        }
    }

    [Fact]
    public async Task Daemon_ClientDisconnect_WithInfiniteKeepAlive_StaysAlive()
    {
        var pipeName = CreateUniquePipeName();
        var (exportProvider, typeRefResolver) = await CreateCompositionAsync();
        var manager = new LanguageServerConnectionManager();
        using var cts = new CancellationTokenSource();

        var daemonTask = Task.Run(() => manager.RunDaemonAsync(pipeName, Timeout.InfiniteTimeSpan, exportProvider, typeRefResolver, CreateLogger(), cts.Token));

        try
        {
            await using (var client = await ConnectClientAsync(pipeName, cts.Token))
            {
                var result = await client.Rpc.InvokeWithParameterObjectAsync<InitializeResult>(
                    Methods.InitializeName, new InitializeParams { Capabilities = new ClientCapabilities() }, cts.Token);
                Assert.NotNull(result.Capabilities);
            }

            // After the only client disconnects, its per-client server tears down...
            await WaitForConditionAsync(() => manager.GetStartedServers().IsEmpty);

            // ...but with an infinite keepalive the daemon itself keeps running.
            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.False(daemonTask.IsCompleted);
        }
        finally
        {
            cts.Cancel();
            Assert.True(await daemonTask);
        }
    }

    // NOTE: Full concurrent multi-client support (multiple Host workspaces in a single process) is tracked by
    // https://github.com/dotnet/roslyn/issues/82917. Until that lands, a second concurrent client's workspace
    // creation may fail. This test verifies the connection-workflow guarantee that such a failure is isolated to
    // that one connection: it must not take down the daemon nor the already-connected client. It remains valid
    // (the catch simply won't trigger) once #82917 makes the second client succeed.
    [Fact]
    public async Task Daemon_SecondConcurrentConnection_IsIsolatedFromDaemonAndFirstClient()
    {
        var pipeName = CreateUniquePipeName();
        var (exportProvider, typeRefResolver) = await CreateCompositionAsync();
        var manager = new LanguageServerConnectionManager();
        using var cts = new CancellationTokenSource();

        var daemonTask = Task.Run(() => manager.RunDaemonAsync(pipeName, Timeout.InfiniteTimeSpan, exportProvider, typeRefResolver, CreateLogger(), cts.Token));

        try
        {
            await using var first = await ConnectClientAsync(pipeName, cts.Token);
            var firstResult = await first.Rpc.InvokeWithParameterObjectAsync<InitializeResult>(
                Methods.InitializeName, new InitializeParams { Capabilities = new ClientCapabilities() }, cts.Token);
            Assert.NotNull(firstResult.Capabilities);

            await using var second = await ConnectClientAsync(pipeName, cts.Token);
            try
            {
                await second.Rpc.InvokeWithParameterObjectAsync<InitializeResult>(
                    Methods.InitializeName, new InitializeParams { Capabilities = new ClientCapabilities() }, cts.Token);
            }
            catch (RemoteRpcException)
            {
                // Expected today (see #82917): the daemon could not create a second Host workspace, so it
                // disposed only that connection.
            }

            // The daemon and the first client must be unaffected by the second connection's outcome.
            Assert.False(daemonTask.IsCompleted);
            Assert.True(manager.GetStartedServers().Length >= 1);
        }
        finally
        {
            cts.Cancel();
            Assert.True(await daemonTask);
        }
    }

    private async Task<(ExportProvider exportProvider, ExtensionTypeRefResolver typeRefResolver)> CreateCompositionAsync()
    {
        var (exportProvider, assemblyLoader) = await LanguageServerTestComposition.CreateExportProviderAsync(
            LoggerFactory, includeDevKitComponents: false, MefCacheDirectory.Path, extensionPaths: null);
        return (exportProvider, new ExtensionTypeRefResolver(assemblyLoader, LoggerFactory));
    }

    private ILogger CreateLogger(string category = "Daemon") => LoggerFactory.CreateLogger(category);

    private static string CreateUniquePipeName() => "roslyn-daemon-test." + Guid.NewGuid().ToString("N");

    private static async Task<TestDaemonClient> ConnectClientAsync(string pipeName, CancellationToken cancellationToken)
    {
        var stream = DaemonNamedPipe.CreateClientStream(pipeName);
        try
        {
            await stream.ConnectAsync(timeout: 30_000, cancellationToken);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }

        var formatter = RoslynLanguageServer.CreateJsonMessageFormatter();
        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, formatter));
        rpc.StartListening();
        return new TestDaemonClient(stream, rpc);
    }

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > s_timeout)
                throw new TimeoutException("The expected condition was not met within the timeout.");

            await Task.Delay(50);
        }
    }

    private sealed class TestDaemonClient(NamedPipeClientStream stream, JsonRpc rpc) : IAsyncDisposable
    {
        public JsonRpc Rpc => rpc;

        public async ValueTask DisposeAsync()
        {
            rpc.Dispose();
            await stream.DisposeAsync();
        }
    }
}
