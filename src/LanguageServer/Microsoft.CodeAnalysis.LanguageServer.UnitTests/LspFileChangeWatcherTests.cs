// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.CodeAnalysis.LanguageServer.HostWorkspace.FileWatching;
using Microsoft.CodeAnalysis.ProjectSystem;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.Extensions.Logging.Abstractions;
using Roslyn.LanguageServer.Protocol;
using StreamJsonRpc;
using Xunit.Abstractions;
using FileSystemWatcher = Roslyn.LanguageServer.Protocol.FileSystemWatcher;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests;

public sealed class LspFileChangeWatcherTests(ITestOutputHelper testOutputHelper)
    : AbstractLanguageServerHostTests(testOutputHelper)
{
    private readonly ClientCapabilities _clientCapabilitiesWithFileWatcherSupport = new()
    {
        Workspace = new WorkspaceClientCapabilities
        {
            DidChangeWatchedFiles = new DidChangeWatchedFilesClientCapabilities { DynamicRegistration = true, RelativePatternSupport = true }
        }
    };

    [Fact]
    public async Task LspFileWatcherNotSupportedWithoutClientSupport()
    {
        await using var testLspServer = await CreateLanguageServerAsync();

        AssertFileWatcherKind<DefaultFileChangeWatcher>(testLspServer);
    }

    [Fact]
    public async Task LspFileWatcherSupportedWithClientSupport()
    {
        await using var testLspServer = await CreateLanguageServerAsync(_clientCapabilitiesWithFileWatcherSupport);

        AssertFileWatcherKind<LspFileChangeWatcher>(testLspServer);
    }

    [Fact]
    public async Task LspFileWatcherRequiresRelativePatternsForOutOfWorkspaceWatches()
    {
        await using var testLspServer = await CreateLanguageServerAsync(new ClientCapabilities
        {
            Workspace = new WorkspaceClientCapabilities
            {
                DidChangeWatchedFiles = new DidChangeWatchedFilesClientCapabilities { DynamicRegistration = true },
            },
        });

        AssertFileWatcherKind<DefaultFileChangeWatcher>(testLspServer);
    }

    [Fact]
    public async Task CreatingDirectoryWatchRequestsDirectoryWatch()
    {
        AsynchronousOperationListenerProvider.Enable(enable: true);

        await using var testLspServer = await CreateLanguageServerAsync(_clientCapabilitiesWithFileWatcherSupport);
        var lspFileChangeWatcher = AssertFileWatcherKind<LspFileChangeWatcher>(testLspServer);

        var dynamicCapabilitiesRpcTarget = new DynamicCapabilitiesRpcTarget();
        testLspServer.AddClientLocalRpcTarget(dynamicCapabilitiesRpcTarget);

        var tempDirectory = TempRoot.CreateDirectory();

        // Try creating a context and ensure we created the registration
        var context = lspFileChangeWatcher.CreateContext([new ProjectSystem.WatchedDirectory(tempDirectory.Path, extensionFilters: [])]);
        await WaitForFileWatcherAsync(testLspServer);

        var watcher = GetSingleFileWatcher(dynamicCapabilitiesRpcTarget);

        Assert.Equal(ProtocolConversions.CreateAbsoluteDocumentUri(tempDirectory.Path), watcher.GlobPattern.Second.BaseUri.Second);
        Assert.Equal("**/*", watcher.GlobPattern.Second.Pattern);

        // Get rid of the registration and it should be gone again
        context.Dispose();
        await WaitForFileWatcherAsync(testLspServer);
        AssertNoFileWatcherRegistration(dynamicCapabilitiesRpcTarget);
    }

    [Fact]
    public async Task CreatingFileWatchRequestsFileWatch()
    {
        AsynchronousOperationListenerProvider.Enable(enable: true);

        await using var testLspServer = await CreateLanguageServerAsync(_clientCapabilitiesWithFileWatcherSupport);
        var lspFileChangeWatcher = AssertFileWatcherKind<LspFileChangeWatcher>(testLspServer);

        var dynamicCapabilitiesRpcTarget = new DynamicCapabilitiesRpcTarget();
        testLspServer.AddClientLocalRpcTarget(dynamicCapabilitiesRpcTarget);

        var tempDirectory = TempRoot.CreateDirectory();

        // Try creating a single file watch and ensure we created the registration
        var context = lspFileChangeWatcher.CreateContext([]);
        var filePath = Path.Combine(tempDirectory.Path, "SingleFile.txt");
        var watchedFile = context.EnqueueWatchingFile(filePath);
        await WaitForFileWatcherAsync(testLspServer);

        var watcher = GetSingleFileWatcher(dynamicCapabilitiesRpcTarget);

        Assert.Equal(ProtocolConversions.CreateAbsoluteDocumentUri(tempDirectory.Path), watcher.GlobPattern.Second.BaseUri.Second);
        Assert.Equal("*.txt", watcher.GlobPattern.Second.Pattern);

        // Get rid of the registration and it should be gone again
        watchedFile.Dispose();
        context.Dispose();
        await WaitForFileWatcherAsync(testLspServer);
        AssertNoFileWatcherRegistration(dynamicCapabilitiesRpcTarget);
    }

    [Theory]
    [InlineData((int)FileChangeType.Created, (int)FileChangeKind.Created)]
    [InlineData((int)FileChangeType.Changed, (int)FileChangeKind.Changed)]
    [InlineData((int)FileChangeType.Deleted, (int)FileChangeKind.Deleted)]
    public async Task FileChangeNotificationIncludesChangeKind(int fileChangeType, int expectedChangeKind)
    {
        await using var testLspServer = await CreateLanguageServerAsync(_clientCapabilitiesWithFileWatcherSupport);
        var lspFileChangeWatcher = AssertFileWatcherKind<LspFileChangeWatcher>(testLspServer);
        testLspServer.AddClientLocalRpcTarget(new DynamicCapabilitiesRpcTarget());
        var tempDirectory = TempRoot.CreateDirectory();
        var filePath = Path.Combine(tempDirectory.Path, "File.cs");

        using var context = lspFileChangeWatcher.CreateContext([new ProjectSystem.WatchedDirectory(tempDirectory.Path, extensionFilters: [])]);
        var fileChangedSource = new TaskCompletionSource<FileChangedEventArgs>();
        context.FileChanged += (_, e) => fileChangedSource.TrySetResult(e);

        await testLspServer.ExecuteNotificationAsync(
            Methods.WorkspaceDidChangeWatchedFilesName,
            new DidChangeWatchedFilesParams
            {
                Changes =
                [
                    new FileEvent
                    {
                        Uri = ProtocolConversions.CreateAbsoluteDocumentUri(filePath),
                        FileChangeType = (FileChangeType)fileChangeType,
                    },
                ],
            });

        var eventArgs = await fileChangedSource.Task;
        Assert.Equal(filePath, eventArgs.FilePath, ignoreCase: true);
        Assert.Equal((FileChangeKind)expectedChangeKind, eventArgs.ChangeKind);
    }

    [Fact]
    public async Task ExactFilesShareDirectoryAndContextDisposalReleasesForgottenTokens()
    {
        AsynchronousOperationListenerProvider.Enable(enable: true);
        await using var server = await CreateLanguageServerAsync(_clientCapabilitiesWithFileWatcherSupport);
        var watcher = AssertFileWatcherKind<LspFileChangeWatcher>(server);
        var client = new DynamicCapabilitiesRpcTarget();
        server.AddClientLocalRpcTarget(client);
        var directory = TempRoot.CreateDirectory().Path;
        var file = Path.Combine(directory, "project.assets.json");
        var first = watcher.CreateContext([]);
        var second = watcher.CreateContext([]);
        var firstToken = first.EnqueueWatchingFile(file);
        second.EnqueueWatchingFile(file);
        second.EnqueueWatchingFile(Path.Combine(directory, "other.json"));
        await WaitForFileWatcherAsync(server);

        Assert.Equal("*.json", GetSingleFileWatcher(client).GlobPattern.Second.Pattern);

        first.Dispose();
        firstToken.Dispose();
        firstToken.Dispose();
        await WaitForFileWatcherAsync(server);
        Assert.Equal("*.json", GetSingleFileWatcher(client).GlobPattern.Second.Pattern);

        second.Dispose();
        second.Dispose();
        await WaitForFileWatcherAsync(server);
        AssertNoFileWatcherRegistration(client);
    }

    [Fact]
    public async Task FlatExactWatchIsNotAbsorbedByRecursiveWatch()
    {
        AsynchronousOperationListenerProvider.Enable(enable: true);
        await using var server = await CreateLanguageServerAsync(_clientCapabilitiesWithFileWatcherSupport);
        var watcher = AssertFileWatcherKind<LspFileChangeWatcher>(server);
        var client = new DynamicCapabilitiesRpcTarget();
        server.AddClientLocalRpcTarget(client);
        var directory = TempRoot.CreateDirectory().Path;
        using var recursive = watcher.CreateContext([new WatchedDirectory(directory, [".json"])]);
        using var flat = watcher.CreateContext([]);
        flat.EnqueueWatchingFile(Path.Combine(directory, "project.assets.json"));
        await WaitForFileWatcherAsync(server);

        var patterns = GetWatchers(client).Select(watch => watch.GlobPattern.Second.Pattern).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["**/*.json", "*.json"], patterns);
    }

    [Fact]
    public async Task SharedPhysicalFilterDoesNotBroadenLogicalFileInterests()
    {
        AsynchronousOperationListenerProvider.Enable(enable: true);
        await using var server = await CreateLanguageServerAsync(_clientCapabilitiesWithFileWatcherSupport);
        var watcher = AssertFileWatcherKind<LspFileChangeWatcher>(server);
        server.AddClientLocalRpcTarget(new DynamicCapabilitiesRpcTarget());
        var directory = TempRoot.CreateDirectory().Path;
        var wanted = Path.Combine(directory, "wanted.json");
        using var context = watcher.CreateContext([]);
        context.EnqueueWatchingFile(wanted);
        var received = new List<string>();
        context.FileChanged += (_, args) => received.Add(args.FilePath);
        await WaitForFileWatcherAsync(server);

        await SendChangesAsync(server,
            new FileEvent { Uri = ProtocolConversions.CreateAbsoluteDocumentUri(Path.Combine(directory, "unrelated.json")), FileChangeType = FileChangeType.Changed },
            new FileEvent { Uri = ProtocolConversions.CreateAbsoluteDocumentUri(wanted), FileChangeType = FileChangeType.Changed });

        Assert.Equal(ProtocolConversions.CreateAbsoluteDocumentUri(wanted).GetRequiredParsedUri().FsPath, Assert.Single(received));
    }

    [Fact]
    public async Task ConsolidationReducesClientDescriptorsNotJustRegistrationRequests()
    {
        AsynchronousOperationListenerProvider.Enable(enable: true);
        await using var server = await CreateLanguageServerAsync(_clientCapabilitiesWithFileWatcherSupport);
        var client = new DynamicCapabilitiesRpcTarget();
        server.AddClientLocalRpcTarget(client);
        var directory = TempRoot.CreateDirectory().Path;
        var listener = server.ExportProvider.GetExportedValue<AsynchronousOperationListenerProvider>().GetListener(FeatureAttribute.Workspace);
        await using var factory = new LspDirectoryWatcherFactory(
            server.GetRequiredLspService<IClientLanguageServerManager>(),
            server.GetRequiredLspService<LspDidChangeWatchedFilesHandler>(),
            listener, [directory], NullLogger.Instance);
        using var watcher = new AggregatingFileChangeWatcher(factory, maxWatcherCount: 4);
        var contexts = new List<IFileChangeContext>();
        try
        {
            for (var i = 0; i < 400; i++)
                contexts.Add(watcher.CreateContext([new WatchedDirectory(Path.Combine(directory, $"project{i}"), [".cs", ".vb"])]));

            await WaitForFileWatcherAsync(server);
            var descriptors = GetWatchers(client).ToArray();
            Assert.InRange(descriptors.Length, 1, 4);
            Assert.All(descriptors, descriptor => Assert.Equal("**/{*.cs,*.vb}", descriptor.GlobPattern.Second.Pattern));

            var received = new List<string>();
            contexts[17].FileChanged += (_, args) => received.Add(args.FilePath);
            var file = Path.Combine(directory, "project17", "file.cs");
            await SendChangesAsync(server,
                new FileEvent { Uri = ProtocolConversions.CreateAbsoluteDocumentUri(file), FileChangeType = FileChangeType.Created });
            Assert.Equal(ProtocolConversions.CreateAbsoluteDocumentUri(file).GetRequiredParsedUri().FsPath, Assert.Single(received));
        }
        finally
        {
            foreach (var context in contexts)
                context.Dispose();
        }

        await WaitForFileWatcherAsync(server);
        AssertNoFileWatcherRegistration(client);
    }

    [Fact]
    public async Task ConnectionsDoNotShareFileNotifications()
    {
        await using var daemon = await CreateDaemonServerAsync();
        await using var firstServer = await daemon.CreateClientAsync(_clientCapabilitiesWithFileWatcherSupport);
        await using var secondServer = await daemon.CreateClientAsync(_clientCapabilitiesWithFileWatcherSupport);
        firstServer.AddClientLocalRpcTarget(new DynamicCapabilitiesRpcTarget());
        secondServer.AddClientLocalRpcTarget(new DynamicCapabilitiesRpcTarget());
        var directory = TempRoot.CreateDirectory().Path;
        using var first = AssertFileWatcherKind<LspFileChangeWatcher>(firstServer).CreateContext([new WatchedDirectory(directory, [".cs"])]);
        using var second = AssertFileWatcherKind<LspFileChangeWatcher>(secondServer).CreateContext([new WatchedDirectory(directory, [".cs"])]);
        var firstEvents = new List<string>();
        var secondEvents = new List<string>();
        first.FileChanged += (_, args) => firstEvents.Add(args.FilePath);
        second.FileChanged += (_, args) => secondEvents.Add(args.FilePath);
        var file = Path.Combine(directory, "file.cs");

        await SendChangesAsync(firstServer,
            new FileEvent { Uri = ProtocolConversions.CreateAbsoluteDocumentUri(file), FileChangeType = FileChangeType.Changed });

        Assert.Equal(ProtocolConversions.CreateAbsoluteDocumentUri(file).GetRequiredParsedUri().FsPath, Assert.Single(firstEvents));
        Assert.Empty(secondEvents);
    }

    private static async Task SendChangesAsync(TestLspServer server, params FileEvent[] changes)
    {
        var handler = server.GetRequiredLspService<LspDidChangeWatchedFilesHandler>();
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnProcessed(object? sender, DidChangeWatchedFilesParams args) => processed.TrySetResult();
        handler.NotificationRaised += OnProcessed;
        try
        {
            await server.ExecuteNotificationAsync(Methods.WorkspaceDidChangeWatchedFilesName, new DidChangeWatchedFilesParams { Changes = changes });
            await processed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            handler.NotificationRaised -= OnProcessed;
        }
    }

    private static T AssertFileWatcherKind<T>(TestLspServer server) where T : IFileChangeWatcher
    {
        var lspFileWatcher = server.GetRequiredLspService<IFileChangeWatcher>();
        var delegatingWatcher = Assert.IsType<DelegatingFileChangeWatcher>(lspFileWatcher);
        return Assert.IsType<T>(delegatingWatcher.GetTestAccessor().UnderlyingFileWatcher);
    }

    private static Task WaitForFileWatcherAsync(TestLspServer testLspServer)
        => testLspServer.ExportProvider.GetExportedValue<AsynchronousOperationListenerProvider>().GetWaiter(FeatureAttribute.Workspace).ExpeditedWaitAsync();

    private static FileSystemWatcher GetSingleFileWatcher(DynamicCapabilitiesRpcTarget dynamicCapabilities)
        => Assert.Single(GetWatchers(dynamicCapabilities));

    private static IEnumerable<FileSystemWatcher> GetWatchers(DynamicCapabilitiesRpcTarget dynamicCapabilities)
    {
        foreach (var registration in dynamicCapabilities.Registrations.Values)
        {
            if (registration.Method == Methods.WorkspaceDidChangeWatchedFilesName)
            {
                var json = Assert.IsType<JsonElement>(registration.RegisterOptions);
                foreach (var watcher in JsonSerializer.Deserialize<DidChangeWatchedFilesRegistrationOptions>(json, ProtocolConversions.LspJsonSerializerOptions)!.Watchers)
                    yield return watcher;
            }
        }
    }

    private static void AssertNoFileWatcherRegistration(DynamicCapabilitiesRpcTarget dynamicCapabilities)
        => Assert.DoesNotContain(dynamicCapabilities.Registrations.Values, static registration => registration.Method == Methods.WorkspaceDidChangeWatchedFilesName);

    private sealed class DynamicCapabilitiesRpcTarget
    {
        public readonly ConcurrentDictionary<string, Registration> Registrations = new();

        [JsonRpcMethod("client/registerCapability", UseSingleObjectParameterDeserialization = true)]
        public async Task RegisterCapabilityAsync(RegistrationParams registrationParams, CancellationToken _)
        {
            foreach (var registration in registrationParams.Registrations)
                Assert.True(Registrations.TryAdd(registration.Id, registration));
        }

        [JsonRpcMethod("client/unregisterCapability", UseSingleObjectParameterDeserialization = true)]
        public async Task UnregisterCapabilityAsync(UnregistrationParams unregistrationParams, CancellationToken _)
        {
            foreach (var unregistration in unregistrationParams.Unregistrations)
                Assert.True(Registrations.TryRemove(unregistration.Id, out var _));
        }
    }
}
