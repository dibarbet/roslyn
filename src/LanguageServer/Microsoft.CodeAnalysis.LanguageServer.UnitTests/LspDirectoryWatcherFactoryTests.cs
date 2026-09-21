// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis.LanguageServer.HostWorkspace.FileWatching;
using Microsoft.CodeAnalysis.ProjectSystem;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.Extensions.Logging;
using Roslyn.LanguageServer.Protocol;
using StreamJsonRpc;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests;

public sealed class LspDirectoryWatcherFactoryTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);
    private static readonly string s_workspacePath = ProtocolConversions.CreateAbsoluteDocumentUri(
        Path.GetFullPath(Path.Combine(nameof(LspDirectoryWatcherFactoryTests), "workspace"))).GetRequiredParsedUri().FsPath;

    [Fact]
    public async Task DisposingWatchBeforeRegistrationAcknowledgementDefersUnregistration()
    {
        await using var context = new TestContext();
        using var pendingRegistration = context.Client.DelayNextRegistration();
        var received = new List<FileChangedEventArgs>();
        var watcher = context.Factory.Create(s_workspacePath, new(["*.cs"], true), received.Add);
        var work = context.WaitAsync();
        var registration = Assert.Single((await pendingRegistration.WaitAsync()).Registrations);

        watcher.Dispose();
        watcher.Dispose();
        context.Notify(Path.Combine(s_workspacePath, "file.cs"));

        Assert.Empty(received);
        Assert.Empty(context.Client.UnregistrationRequests);
        Assert.False(work.IsCompleted);

        pendingRegistration.Acknowledge();
        await work;
        await context.WaitAsync();

        Assert.Equal(registration.Id, Assert.Single(Assert.Single(context.Client.UnregistrationRequests).Unregistrations).Id);
        Assert.Empty(context.Client.ActiveRegistrations);
    }

    [Fact]
    public async Task DisposingFactoryWaitsForPendingRegistrationAndUnregistration()
    {
        await using var context = new TestContext();
        using var pendingRegistration = context.Client.DelayNextRegistration();
        using var pendingUnregistration = context.Client.DelayNextUnregistration();
        var received = new List<FileChangedEventArgs>();
        using var watcher = context.Factory.Create(s_workspacePath, new(["*.cs"], true), received.Add);
        var work = context.WaitAsync();
        var registration = Assert.Single((await pendingRegistration.WaitAsync()).Registrations);

        var disposal = context.Factory.DisposeAsync().AsTask();
        var repeatedDisposal = context.Factory.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        Assert.False(repeatedDisposal.IsCompleted);
        Assert.Empty(context.Client.UnregistrationRequests);
        context.Notify(Path.Combine(s_workspacePath, "file.cs"));
        Assert.Empty(received);

        pendingRegistration.Acknowledge();
        var unregistration = Assert.Single((await pendingUnregistration.WaitAsync()).Unregistrations);
        Assert.Equal(registration.Id, unregistration.Id);
        Assert.False(disposal.IsCompleted);
        Assert.Single(context.Client.ActiveRegistrations);

        pendingUnregistration.Acknowledge();
        await Task.WhenAll(disposal, repeatedDisposal).WaitAsync(s_timeout);
        await work;

        Assert.Empty(context.Client.ActiveRegistrations);
        await context.Factory.DisposeAsync();
        Assert.Single(context.Client.UnregistrationRequests);
        Assert.Throws<ObjectDisposedException>(() => context.Factory.Create(s_workspacePath, new([], true), static _ => { }));
    }

    [Theory]
    [InlineData((int)FileChangeType.Created, (int)FileChangeKind.Created)]
    [InlineData((int)FileChangeType.Changed, (int)FileChangeKind.Changed)]
    [InlineData((int)FileChangeType.Deleted, (int)FileChangeKind.Deleted)]
    public async Task ParentReplacementReceivesNotificationsBeforeAcknowledgementAndRetainsChildCoverage(
        int fileChangeType, int expectedChangeKind)
    {
        await using var context = new TestContext();
        var childPath = Path.Combine(s_workspacePath, "child");
        var childChanges = new List<FileChangedEventArgs>();
        var parentChanges = new List<FileChangedEventArgs>();
        using var child = context.Factory.Create(childPath, new(["*.cs"], true), childChanges.Add);
        await context.WaitAsync();
        var childRegistration = Assert.Single(context.Client.ActiveRegistrations).Value;

        using var pendingRegistration = context.Client.DelayNextRegistration();
        IDirectoryWatcher parent;
        using (context.Factory.BeginUpdate())
        {
            child.Dispose();
            parent = context.Factory.Create(s_workspacePath, new(["*.cs"], true), parentChanges.Add);
        }

        using var parentLifetime = parent;
        var work = context.WaitAsync();
        var parentRegistration = Assert.Single((await pendingRegistration.WaitAsync()).Registrations);
        Assert.Equal(ProtocolConversions.CreateAbsoluteDocumentUri(s_workspacePath),
            Assert.Single(GetWatchers(parentRegistration)).GlobPattern.Second.BaseUri.Second);
        Assert.Equal(childRegistration.Id, Assert.Single(context.Client.ActiveRegistrations).Key);
        Assert.Empty(context.Client.UnregistrationRequests);

        var filePath = Path.Combine(childPath, "file.cs");
        context.Notify(filePath, (FileChangeType)fileChangeType);
        Assert.Empty(childChanges);
        var change = Assert.Single(parentChanges);
        Assert.Equal(filePath, change.FilePath);
        Assert.Equal((FileChangeKind)expectedChangeKind, change.ChangeKind);

        pendingRegistration.Acknowledge();
        await work;

        Assert.Equal(parentRegistration.Id, Assert.Single(context.Client.ActiveRegistrations).Key);
        Assert.Equal(childRegistration.Id, Assert.Single(Assert.Single(context.Client.UnregistrationRequests).Unregistrations).Id);
    }

    [Fact]
    public async Task UpdatingWatchUsesDesiredFiltersBeforeReplacementAcknowledgement()
    {
        await using var context = new TestContext();
        var received = new List<FileChangedEventArgs>();
        using var watcher = context.Factory.Create(s_workspacePath, new(["*.cs"], true), received.Add);
        await context.WaitAsync();
        var oldRegistration = Assert.Single(context.Client.ActiveRegistrations).Value;

        using var pendingRegistration = context.Client.DelayNextRegistration();
        watcher.Update(new(["*.vb"], false));
        var work = context.WaitAsync();
        var replacement = Assert.Single((await pendingRegistration.WaitAsync()).Registrations);
        Assert.Equal("*.vb", Assert.Single(GetWatchers(replacement)).GlobPattern.Second.Pattern);
        Assert.Equal(oldRegistration.Id, Assert.Single(context.Client.ActiveRegistrations).Key);
        Assert.Empty(context.Client.UnregistrationRequests);

        var wanted = Path.Combine(s_workspacePath, "file.vb");
        context.Notify(Path.Combine(s_workspacePath, "file.cs"));
        context.Notify(Path.Combine(s_workspacePath, "child", "file.vb"));
        context.Notify(wanted);
        Assert.Equal(wanted, Assert.Single(received).FilePath);

        pendingRegistration.Acknowledge();
        await work;

        Assert.Equal(replacement.Id, Assert.Single(context.Client.ActiveRegistrations).Key);
        Assert.Equal(oldRegistration.Id, Assert.Single(Assert.Single(context.Client.UnregistrationRequests).Unregistrations).Id);
    }

    [Fact]
    public async Task FailedRegistrationRetainsPreviousCoverageAndCleansUpUncertainRegistrationOnNextMutation()
    {
        await using var context = new TestContext();
        using var watcher = context.Factory.Create(s_workspacePath, new(["*.cs"], true), static _ => { });
        await context.WaitAsync();
        var oldRegistration = Assert.Single(context.Client.ActiveRegistrations).Value;

        using var pendingRegistration = context.Client.DelayNextRegistration();
        watcher.Update(new(["*.cs", "*.vb"], true));
        var work = context.WaitAsync();
        var failedRegistration = Assert.Single((await pendingRegistration.WaitAsync()).Registrations);
        var failure = new RemoteInvocationException("Registration rejected", -32603, errorData: null);
        pendingRegistration.Fail(failure);
        await work;

        Assert.Equal(oldRegistration.Id, Assert.Single(context.Client.ActiveRegistrations).Key);
        Assert.Empty(context.Client.UnregistrationRequests);
        var error = Assert.Single(context.Logger.Errors);
        Assert.Same(failure, error.Exception);
        Assert.Contains(s_workspacePath, error.Message);

        watcher.Update(new(["*.cs", "*.vb", "*.json"], true));
        await context.WaitAsync();

        var replacement = Assert.Single(context.Client.ActiveRegistrations).Value;
        Assert.Equal("**/{*.cs,*.vb,*.json}", Assert.Single(GetWatchers(replacement)).GlobPattern.Second.Pattern);
        Assert.Equal(
            new[] { oldRegistration.Id, failedRegistration.Id }.Order(),
            context.Client.UnregistrationRequests.SelectMany(request => request.Unregistrations).Select(registration => registration.Id).Order());
        Assert.Equal(3, context.Client.RegistrationRequests.Count);
    }

    [Fact]
    public async Task FailedUnregistrationIsRetainedForRetryOnNextMutation()
    {
        await using var context = new TestContext();
        using var watcher = context.Factory.Create(s_workspacePath, new(["*.cs"], true), static _ => { });
        await context.WaitAsync();
        var oldRegistration = Assert.Single(context.Client.ActiveRegistrations).Value;

        using var pendingUnregistration = context.Client.DelayNextUnregistration();
        watcher.Dispose();
        var work = context.WaitAsync();
        Assert.Equal(oldRegistration.Id, Assert.Single((await pendingUnregistration.WaitAsync()).Unregistrations).Id);
        var failure = new RemoteInvocationException("Unregistration rejected", -32603, errorData: null);
        pendingUnregistration.Fail(failure);
        await work;

        Assert.Equal(oldRegistration.Id, Assert.Single(context.Client.ActiveRegistrations).Key);
        var error = Assert.Single(context.Logger.Errors);
        Assert.Same(failure, error.Exception);
        Assert.Contains(oldRegistration.Id, error.Message);

        using var replacement = context.Factory.Create(Path.Combine(s_workspacePath, "other"), new(["*.vb"], true), static _ => { });
        await context.WaitAsync();

        Assert.NotEqual(oldRegistration.Id, Assert.Single(context.Client.ActiveRegistrations).Key);
        Assert.Equal(
            new[] { oldRegistration.Id, oldRegistration.Id },
            context.Client.UnregistrationRequests.SelectMany(request => request.Unregistrations).Select(registration => registration.Id));
    }

    [Fact]
    public async Task NestedUpdateScopesRegisterOnlyFinalDesiredOptions()
    {
        await using var context = new TestContext();
        IDirectoryWatcher watcher;
        using (context.Factory.BeginUpdate())
        {
            watcher = context.Factory.Create(s_workspacePath, new(["*.cs"], true), static _ => { });
            using (context.Factory.BeginUpdate())
                watcher.Update(new(["*.vb"], true));

            watcher.Update(new(["*.cs", "*.vb"], false));
        }

        using var lifetime = watcher;
        await context.WaitAsync();

        var registration = Assert.Single(Assert.Single(context.Client.RegistrationRequests).Registrations);
        Assert.Equal("client/registerCapability", Assert.Single(context.Client.Methods));
        Assert.Equal(Methods.WorkspaceDidChangeWatchedFilesName, registration.Method);
        Assert.Equal("{*.cs,*.vb}", Assert.Single(GetWatchers(registration)).GlobPattern.Second.Pattern);
        Assert.Empty(context.Client.UnregistrationRequests);
    }

    [Fact]
    public async Task WatchCreatedAndDisposedWithinUpdateScopeDoesNotRegister()
    {
        await using var context = new TestContext();
        using (context.Factory.BeginUpdate())
        {
            using var watcher = context.Factory.Create(s_workspacePath, new(["*.cs"], true), static _ => { });
            watcher.Update(new(["*.vb"], false));
        }

        await context.WaitAsync();
        Assert.Empty(context.Client.RegistrationRequests);
        Assert.Empty(context.Client.UnregistrationRequests);
    }

    [Fact]
    public async Task EquivalentUpdateAndRepeatedDisposalDoNotSendRedundantRequests()
    {
        await using var context = new TestContext();
        var watcher = context.Factory.Create(s_workspacePath, new(["*.cs", "*.vb"], true), static _ => { });
        await context.WaitAsync();

        watcher.Update(new(["*.cs", "*.vb"], true));
        await context.WaitAsync();
        Assert.Single(context.Client.RegistrationRequests);
        Assert.Empty(context.Client.UnregistrationRequests);

        watcher.Dispose();
        watcher.Dispose();
        watcher.Update(new([], false));
        await context.WaitAsync();
        await context.Factory.DisposeAsync();
        await context.Factory.DisposeAsync();

        Assert.Single(context.Client.RegistrationRequests);
        var unregistration = Assert.Single(Assert.Single(context.Client.UnregistrationRequests).Unregistrations);
        Assert.Equal(Methods.WorkspaceDidChangeWatchedFilesName, unregistration.Method);
        Assert.Empty(context.Client.ActiveRegistrations);
    }

    [Fact]
    public async Task FactoriesDoNotShareRegistrationsOrNotifications()
    {
        await using var first = new TestContext();
        await using var second = new TestContext();
        var firstChanges = new List<FileChangedEventArgs>();
        var secondChanges = new List<FileChangedEventArgs>();
        using var firstWatcher = first.Factory.Create(s_workspacePath, new(["*.cs"], true), firstChanges.Add);
        using var secondWatcher = second.Factory.Create(s_workspacePath, new(["*.cs"], true), secondChanges.Add);
        await Task.WhenAll(first.WaitAsync(), second.WaitAsync());

        Assert.NotEqual(Assert.Single(first.Client.ActiveRegistrations).Key, Assert.Single(second.Client.ActiveRegistrations).Key);
        var file = Path.Combine(s_workspacePath, "file.cs");
        first.Notify(file);
        Assert.Equal(file, Assert.Single(firstChanges).FilePath);
        Assert.Empty(secondChanges);

        await first.Factory.DisposeAsync();
        Assert.Empty(first.Client.ActiveRegistrations);
        Assert.Single(second.Client.ActiveRegistrations);
        second.Notify(file);
        Assert.Equal(file, Assert.Single(secondChanges).FilePath);
        first.Notify(file);
        Assert.Single(firstChanges);
        Assert.Empty(second.Client.UnregistrationRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationsRespectDirectoryBoundaryRecursionAndMixedExtensions(bool includeSubdirectories)
    {
        await using var context = new TestContext();
        var received = new List<FileChangedEventArgs>();
        using var watcher = context.Factory.Create(
            s_workspacePath + Path.DirectorySeparatorChar, new(["*.cs", "*.vb"], includeSubdirectories), received.Add);
        await context.WaitAsync();

        var csharpFile = Path.Combine(s_workspacePath, "file.cs");
        var visualBasicFile = Path.Combine(s_workspacePath, "file.vb");
        var nestedFile = Path.Combine(s_workspacePath, "child", "file.cs");
        context.Notify(csharpFile);
        context.Notify(visualBasicFile);
        context.Notify(nestedFile);
        context.Notify(Path.Combine(s_workspacePath, "file.csx"));
        context.Notify(Path.Combine(s_workspacePath, "file.txt"));
        context.Notify(Path.Combine(s_workspacePath + "Sibling", "file.cs"));
        context.Notify(Path.Combine(Path.GetDirectoryName(s_workspacePath)!, "file.cs"));

        string[] expected = includeSubdirectories ? [csharpFile, visualBasicFile, nestedFile] : [csharpFile, visualBasicFile];
        Assert.Equal(expected, received.Select(change => change.FilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllFilesOptionsIncludeExtensionlessFiles(bool explicitWildcard)
    {
        await using var context = new TestContext();
        var received = new List<FileChangedEventArgs>();
        using var watcher = context.Factory.Create(
            s_workspacePath, new(explicitWildcard ? ["*.cs", "*"] : [], false), received.Add);
        await context.WaitAsync();

        var file = Path.Combine(s_workspacePath, "README");
        context.Notify(file);
        Assert.Equal(file, Assert.Single(received).FilePath);
    }

    [Fact]
    public async Task PathAndExtensionMatchingUsePlatformComparison()
    {
        await using var context = new TestContext();
        var received = new List<FileChangedEventArgs>();
        using var watcher = context.Factory.Create(s_workspacePath, new(["*.cs"], true), received.Add);
        await context.WaitAsync();

        context.Notify(Path.Combine(s_workspacePath.ToUpperInvariant(), "FILE.CS"));
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Assert.Equal(StringComparison.Ordinal, context.Factory.PathComparison);
            Assert.Empty(received);
        }
        else
        {
            Assert.Equal(StringComparison.OrdinalIgnoreCase, context.Factory.PathComparison);
            Assert.Single(received);
        }
    }

    [Fact]
    public async Task ConsolidationIsLimitedToWorkspaceBoundaries()
    {
        await using var context = new TestContext([s_workspacePath + Path.DirectorySeparatorChar]);
        Assert.True(context.Factory.CanConsolidate(s_workspacePath));
        Assert.True(context.Factory.CanConsolidate(Path.Combine(s_workspacePath, "child")));
        Assert.False(context.Factory.CanConsolidate(Path.GetDirectoryName(s_workspacePath)!));
        Assert.False(context.Factory.CanConsolidate(s_workspacePath + "Sibling"));
        Assert.False(context.Factory.CanConsolidate(Path.GetPathRoot(s_workspacePath)!));
        Assert.True(context.Factory.CanWatchDirectory(Path.Combine(s_workspacePath, "not-created")));
        Assert.True(context.Factory.SeparateFlatWatches);
    }

    [Fact]
    public async Task UnknownWorkspaceDoesNotPermitConsolidation()
    {
        await using var context = new TestContext([]);
        Assert.False(context.Factory.CanConsolidate(s_workspacePath));
        Assert.False(context.Factory.CanConsolidate(Path.Combine(s_workspacePath, "child")));
    }

    [Fact]
    public async Task FilesystemRootCannotBeConsolidatedEvenWhenItIsAWorkspace()
    {
        var root = Path.GetPathRoot(s_workspacePath)!;
        await using var context = new TestContext([root]);
        Assert.False(context.Factory.CanConsolidate(root));
        Assert.True(context.Factory.CanConsolidate(s_workspacePath));
    }

    [Fact]
    public async Task FilesystemRootRegistrationPreservesAbsoluteBaseUri()
    {
        var root = Path.GetPathRoot(s_workspacePath)!;
        await using var context = new TestContext([root]);
        using var watcher = context.Factory.Create(root, new([], true), static _ => { });
        await context.WaitAsync();

        var registration = Assert.Single(Assert.Single(context.Client.RegistrationRequests).Registrations);
        var relativePattern = Assert.Single(GetWatchers(registration)).GlobPattern.Second;
        Assert.Equal(ProtocolConversions.CreateAbsoluteDocumentUri(root), relativePattern.BaseUri.Second);
        Assert.True(Path.IsPathFullyQualified(relativePattern.BaseUri.Second.GetRequiredParsedUri().FsPath));
        Assert.Equal("**/*", relativePattern.Pattern);
    }

    [Theory]
    [InlineData(false, new string[] { }, "*")]
    [InlineData(true, new string[] { }, "**/*")]
    [InlineData(false, new[] { "*.cs" }, "*.cs")]
    [InlineData(true, new[] { "*.cs" }, "**/*.cs")]
    [InlineData(false, new[] { "*.cs", "*.vb" }, "{*.cs,*.vb}")]
    [InlineData(true, new[] { "*.cs", "*.vb" }, "**/{*.cs,*.vb}")]
    [InlineData(false, new[] { "*.cs", "*" }, "*")]
    [InlineData(true, new[] { "*", "*.vb" }, "**/*")]
    public void PatternsRepresentFiltersAndRecursion(bool includeSubdirectories, string[] filters, string expectedPattern)
    {
        var patterns = LspDirectoryWatcherFactory.GetPatterns(new([.. filters], includeSubdirectories));
        Assert.Equal(expectedPattern, Assert.Single(patterns));
    }

    [Theory]
    [InlineData("*.a*b", "*.a[*]b")]
    [InlineData("*.a?b", "*.a[?]b")]
    [InlineData("*.a[b]", "*.a[[]b[]]")]
    [InlineData("*.a{b}", "*.a[{]b[}]")]
    [InlineData("*.a,b", "*.a[,]b")]
    public void PatternsEscapeLiteralGlobMetacharacters(string filter, string escapedFilter)
    {
        var patterns = LspDirectoryWatcherFactory.GetPatterns(new([filter, "*.cs"], true));
        Assert.Equal("**/{" + escapedFilter + ",*.cs}", Assert.Single(patterns));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongFilterListsProduceBoundedPatternsAndAccurateDescriptorCounts(bool includeSubdirectories)
    {
        await using var context = new TestContext();
        var filters = Enumerable.Range(0, 5).Select(i => $"*.{i}{new string('x', 2000)}").ToImmutableArray();
        var options = new DirectoryWatchOptions(filters, includeSubdirectories);
        var patterns = LspDirectoryWatcherFactory.GetPatterns(options);
        var prefix = includeSubdirectories ? "**/" : "";
        Assert.Equal(
            new[] { prefix + "{" + filters[0] + "," + filters[1] + "}", prefix + "{" + filters[2] + "," + filters[3] + "}", prefix + filters[4] },
            patterns);
        Assert.All(patterns, pattern => Assert.InRange(pattern.Length, 1, 4096));
        Assert.Equal(3, context.Factory.GetWatchCount(options));

        using var watcher = context.Factory.Create(s_workspacePath, options, static _ => { });
        await context.WaitAsync();

        var registration = Assert.Single(Assert.Single(context.Client.RegistrationRequests).Registrations);
        Assert.Equal(patterns, GetWatchers(registration).Select(watcher => watcher.GlobPattern.Second.Pattern));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SinglePatternCanReachButNotExceedLengthLimit(bool includeSubdirectories)
    {
        var prefixLength = includeSubdirectories ? 3 : 0;
        var filter = "*" + new string('x', 4095 - prefixLength);
        Assert.Equal(4096, Assert.Single(LspDirectoryWatcherFactory.GetPatterns(new([filter], includeSubdirectories))).Length);
        Assert.Throws<ArgumentException>(() => LspDirectoryWatcherFactory.GetPatterns(new([filter + "x"], includeSubdirectories)));
    }

    [Fact]
    public void PatternLengthLimitAccountsForEscaping()
    {
        var filter = "*." + new string('?', 1500);
        Assert.Throws<ArgumentException>(() => LspDirectoryWatcherFactory.GetPatterns(new([filter], false)));
    }

    private static Roslyn.LanguageServer.Protocol.FileSystemWatcher[] GetWatchers(Registration registration)
        => Assert.IsType<DidChangeWatchedFilesRegistrationOptions>(registration.RegisterOptions).Watchers;

    private sealed class TestContext : IAsyncDisposable
    {
        private readonly AsynchronousOperationListener _listener = new(FeatureAttribute.Workspace, enableDiagnosticTokens: true);
        private readonly LspDidChangeWatchedFilesHandler _handler = new();

        public TestClient Client { get; } = new();
        public TestLogger Logger { get; } = new();
        public LspDirectoryWatcherFactory Factory { get; }

        public TestContext()
            : this([s_workspacePath])
        {
        }

        public TestContext(ImmutableArray<string> workspacePaths)
            => Factory = new(Client, _handler, _listener, workspacePaths, Logger);

        public Task WaitAsync() => _listener.ExpeditedWaitAsync().WaitAsync(s_timeout);

        public void Notify(string filePath, FileChangeType kind = FileChangeType.Changed)
            => _handler.Notify(new DidChangeWatchedFilesParams
            {
                Changes = [new FileEvent { Uri = ProtocolConversions.CreateAbsoluteDocumentUri(filePath), FileChangeType = kind }],
            });

        public async ValueTask DisposeAsync()
        {
            Client.ReleasePendingRequests();
            var disposal = Factory.DisposeAsync().AsTask();
            await WaitAsync();
            await disposal.WaitAsync(s_timeout);
        }
    }

    private sealed class TestClient : IClientLanguageServerManager
    {
        private readonly ConcurrentQueue<RequestGate<RegistrationParams>> _registrationGates = new();
        private readonly ConcurrentQueue<RequestGate<UnregistrationParams>> _unregistrationGates = new();
        private readonly ConcurrentBag<IDisposable> _gates = [];

        public ConcurrentQueue<string> Methods { get; } = new();
        public ConcurrentQueue<RegistrationParams> RegistrationRequests { get; } = new();
        public ConcurrentQueue<UnregistrationParams> UnregistrationRequests { get; } = new();
        public ConcurrentDictionary<string, Registration> ActiveRegistrations { get; } = new();

        public RequestGate<RegistrationParams> DelayNextRegistration()
        {
            var gate = new RequestGate<RegistrationParams>();
            _registrationGates.Enqueue(gate);
            _gates.Add(gate);
            return gate;
        }

        public RequestGate<UnregistrationParams> DelayNextUnregistration()
        {
            var gate = new RequestGate<UnregistrationParams>();
            _unregistrationGates.Enqueue(gate);
            _gates.Add(gate);
            return gate;
        }

        public void ReleasePendingRequests()
        {
            foreach (var gate in _gates)
                gate.Dispose();
        }

        public async ValueTask SendRequestAsync<TParams>(string methodName, TParams @params, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Methods.Enqueue(methodName);
            switch (@params)
            {
                case RegistrationParams registrationParams when methodName == "client/registerCapability":
                    RegistrationRequests.Enqueue(registrationParams);
                    if (_registrationGates.TryDequeue(out var registrationGate))
                        await registrationGate.WaitForAcknowledgementAsync(registrationParams);
                    foreach (var registration in registrationParams.Registrations)
                        ActiveRegistrations[registration.Id] = registration;
                    break;

                case UnregistrationParams unregistrationParams when methodName == "client/unregisterCapability":
                    UnregistrationRequests.Enqueue(unregistrationParams);
                    if (_unregistrationGates.TryDequeue(out var unregistrationGate))
                        await unregistrationGate.WaitForAcknowledgementAsync(unregistrationParams);
                    foreach (var unregistration in unregistrationParams.Unregistrations)
                        ActiveRegistrations.TryRemove(unregistration.Id, out _);
                    break;

                default:
                    throw new InvalidOperationException($"Unexpected request: {methodName}");
            }
        }

        public Task<TResponse> SendRequestAsync<TParams, TResponse>(string methodName, TParams @params, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public ValueTask SendRequestAsync(string methodName, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public ValueTask SendNotificationAsync(string methodName, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public ValueTask SendNotificationAsync<TParams>(string methodName, TParams @params, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    private sealed class RequestGate<T> : IDisposable
    {
        private readonly TaskCompletionSource<T> _received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _acknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> WaitAsync() => _received.Task.WaitAsync(s_timeout);

        public async Task WaitForAcknowledgementAsync(T request)
        {
            _received.SetResult(request);
            await _acknowledgement.Task;
        }

        public void Acknowledge() => _acknowledgement.TrySetResult();
        public void Fail(Exception exception) => _acknowledgement.TrySetException(exception);
        public void Dispose() => Acknowledge();
    }

    private sealed class TestLogger : ILogger
    {
        public ConcurrentQueue<(Exception? Exception, string Message)> Errors { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
                Errors.Enqueue((exception, formatter(state, exception)));
        }
    }
}
