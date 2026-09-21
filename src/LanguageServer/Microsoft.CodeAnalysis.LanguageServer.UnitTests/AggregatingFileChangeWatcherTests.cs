// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.LanguageServer.HostWorkspace.FileWatching;
using Microsoft.CodeAnalysis.ProjectSystem;
using Roslyn.Test.Utilities;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests;

public sealed class AggregatingFileChangeWatcherTests
{
    private static readonly string s_root = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!, "WatcherTests", "workspace");

    [Fact]
    public void SameDirectorySharesCompleteConfigurationAndPreservesContextFiltering()
    {
        var factory = new RecordingFactory();
        var watcher = new AggregatingFileChangeWatcher(factory, maxWatcherCount: 10);
        using var first = watcher.CreateContext([new(s_root, [".cs"])]);
        using var second = watcher.CreateContext([new(s_root, [".vb"])]);
        var directory = Assert.Single(factory.LiveWatchers);
        AssertEx.SetEqual(["*.cs", "*.vb"], directory.Options.Filters);

        var firstChanges = new List<string>();
        var secondChanges = new List<string>();
        first.FileChanged += (_, e) => firstChanges.Add(e.FilePath);
        second.FileChanged += (_, e) => secondChanges.Add(e.FilePath);
        directory.Raise(new(Path.Combine(s_root, "one.cs"), FileChangeKind.Created));
        directory.Raise(new(Path.Combine(s_root, "two.vb"), FileChangeKind.Changed));
        directory.Raise(new(Path.Combine(s_root, "three.txt"), FileChangeKind.Deleted));
        Assert.Equal([Path.Combine(s_root, "one.cs")], firstChanges);
        Assert.Equal([Path.Combine(s_root, "two.vb")], secondChanges);

        first.Dispose();
        Assert.Same(second, Assert.Single(AggregatingFileChangeWatcher.TestAccessor.GetActiveContexts(watcher)));
        Assert.Same(directory, Assert.Single(factory.LiveWatchers));
        second.Dispose();
        Assert.Empty(factory.LiveWatchers);
        Assert.Empty(AggregatingFileChangeWatcher.TestAccessor.GetWatchedDirectories(watcher));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void SeparateFlatWatchesAreNotAbsorbedByRecursiveCoverage(bool recursiveFirst, bool sameDirectory)
    {
        var factory = new RecordingFactory { SeparateFlatWatches = true };
        var watcher = new AggregatingFileChangeWatcher(factory, maxWatcherCount: 1);
        var explicitPath = Path.Combine(sameDirectory ? s_root : Path.Combine(s_root, "obj"), "project.assets.json");
        using var exactContext = watcher.CreateContext([]);
        IFileChangeContext recursiveContext;
        IWatchedFile file;

        if (recursiveFirst)
        {
            recursiveContext = watcher.CreateContext([new(s_root, [])]);
            file = exactContext.EnqueueWatchingFile(explicitPath);
        }
        else
        {
            file = exactContext.EnqueueWatchingFile(explicitPath);
            recursiveContext = watcher.CreateContext([new(s_root, [])]);
        }

        using (recursiveContext)
        using (file)
        {
            Assert.Equal(2, factory.LiveWatchers.Count());
            var flat = Assert.Single(factory.LiveWatchers.Where(static w => !w.Options.IncludeSubdirectories));
            Assert.Single(factory.LiveWatchers.Where(static w => w.Options.IncludeSubdirectories));
            Assert.Equal("*.json", Assert.Single(flat.Options.Filters));
            recursiveContext.Dispose();

            var changes = new List<string>();
            exactContext.FileChanged += (_, e) => changes.Add(e.FilePath);
            flat.Raise(new(explicitPath, FileChangeKind.Changed));
            flat.Raise(new(Path.Combine(Path.GetDirectoryName(explicitPath)!, "unrelated.json"), FileChangeKind.Changed));
            Assert.Equal([explicitPath], changes);
        }

        Assert.Empty(factory.LiveWatchers);
        Assert.Empty(AggregatingFileChangeWatcher.TestAccessor.GetWatchedDirectories(watcher));
    }

    [Fact]
    public async Task ConsolidationInstallsParentFirstAndForwardsCapturedChildEvents()
    {
        var childPath = Path.Combine(s_root, "child");
        var filePath = Path.Combine(childPath, "one.cs");
        var factory = new RecordingFactory();
        var watcher = new AggregatingFileChangeWatcher(factory, maxWatcherCount: 10);
        using var childContext = watcher.CreateContext([new(childPath, [".cs"])]);
        var child = Assert.Single(factory.LiveWatchers);
        var receivedDuringHandover = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        childContext.FileChanged += (_, _) => receivedDuringHandover.TrySetResult(true);
        factory.Operations.Clear();
        factory.BeforeCreate = (path, options) =>
        {
            Assert.Equal(s_root, path);
            AssertEx.SetEqual(["*.cs", "*.vb"], options.Filters);
            Assert.False(child.Disposed);
            child.Raise(new(filePath, FileChangeKind.Changed));
        };

        using var parentContext = watcher.CreateContext([new(s_root, [".vb"])]);
        Assert.Equal([("Create", s_root), ("Dispose", childPath)], factory.Operations);
        await receivedDuringHandover.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(child.Disposed);

        var afterHandover = new List<string>();
        childContext.FileChanged += (_, e) => afterHandover.Add(e.FilePath);
        child.Raise(new(filePath, FileChangeKind.Deleted));
        Assert.Equal([filePath], afterHandover);
    }

    [Fact]
    public void ConsolidationExpandsExistingParentBeforeRemovingChildren()
    {
        var childPath = Path.Combine(s_root, "child");
        var factory = new RecordingFactory();
        var watcher = new AggregatingFileChangeWatcher(factory, maxWatcherCount: 10);
        using var flatContext = watcher.CreateContext([]);
        using var file = flatContext.EnqueueWatchingFile(Path.Combine(s_root, "one.fs"));
        using var childContext = watcher.CreateContext([new(childPath, [".cs"])]);
        factory.Operations.Clear();

        using var parentContext = watcher.CreateContext([new(s_root, [".vb"])]);
        Assert.Equal([("Update", s_root), ("Dispose", childPath)], factory.Operations);
        var parent = Assert.Single(factory.LiveWatchers);
        Assert.True(parent.Options.IncludeSubdirectories);
        AssertEx.SetEqual(["*.fs", "*.cs", "*.vb"], parent.Options.Filters);
    }

    [Fact]
    public void FailedParentCreationRetainsPreviousCoverageAndOwnership()
    {
        var childPath = Path.Combine(s_root, "child");
        var filePath = Path.Combine(childPath, "one.cs");
        var factory = new RecordingFactory();
        var watcher = new AggregatingFileChangeWatcher(factory, maxWatcherCount: 10);
        using var context = watcher.CreateContext([new(childPath, [".cs"])]);
        var child = Assert.Single(factory.LiveWatchers);
        factory.BeforeCreate = (_, _) => throw new IOException("Cannot create replacement");

        Assert.Throws<IOException>(() => watcher.CreateContext([new(s_root, [".vb"])]));
        Assert.Same(child, Assert.Single(factory.LiveWatchers));
        Assert.Same(context, Assert.Single(AggregatingFileChangeWatcher.TestAccessor.GetActiveContexts(watcher)));
        var changes = new List<string>();
        context.FileChanged += (_, e) => changes.Add(e.FilePath);
        child.Raise(new(filePath, FileChangeKind.Created));
        Assert.Equal([filePath], changes);
    }

    [Fact]
    public void FailedUpdateDoesNotRemoveAnExistingWatchFromTheSameContext()
    {
        var factory = new RecordingFactory();
        var watcher = new AggregatingFileChangeWatcher(factory, maxWatcherCount: 10);
        using var context = watcher.CreateContext([]);
        var filePath = Path.Combine(s_root, "one.cs");
        using var file = context.EnqueueWatchingFile(filePath);
        var directory = Assert.Single(factory.LiveWatchers);
        factory.BeforeUpdate = (_, _) => throw new IOException("Cannot expand watch");

        Assert.Throws<IOException>(() => context.EnqueueWatchingFile(Path.Combine(s_root, "two.vb")));
        Assert.Equal("*.cs", Assert.Single(directory.Options.Filters));
        Assert.Same(context, Assert.Single(AggregatingFileChangeWatcher.TestAccessor.GetActiveContexts(watcher)));
        var changes = new List<string>();
        context.FileChanged += (_, e) => changes.Add(e.FilePath);
        directory.Raise(new(filePath, FileChangeKind.Changed));
        Assert.Equal([filePath], changes);
    }

    [Fact]
    public void FailedContextConstructionReleasesPreviouslyAcquiredDirectories()
    {
        var factory = new RecordingFactory();
        var watcher = new AggregatingFileChangeWatcher(factory, maxWatcherCount: 10);
        var secondPath = Path.Combine(s_root, "second");
        factory.BeforeCreate = (path, _) =>
        {
            if (path == secondPath)
                throw new IOException("Cannot watch second directory");
        };

        Assert.Throws<IOException>(() => watcher.CreateContext([new(Path.Combine(s_root, "first"), [".cs"]), new(secondPath, [".vb"])]));
        Assert.Empty(factory.LiveWatchers);
        Assert.Empty(AggregatingFileChangeWatcher.TestAccessor.GetActiveContexts(watcher));
        Assert.Empty(AggregatingFileChangeWatcher.TestAccessor.GetWatchedDirectories(watcher));
    }

    [Fact]
    public void TokensAndContextDisposalReleaseAllOwners()
    {
        var factory = new RecordingFactory();
        var watcher = new AggregatingFileChangeWatcher(factory, maxWatcherCount: 10);
        using var context = watcher.CreateContext([]);
        var filePath = Path.Combine(s_root, "one.cs");
        using var first = context.EnqueueWatchingFile(filePath);
        using var second = context.EnqueueWatchingFile(filePath);
        Assert.Single(factory.LiveWatchers);
        first.Dispose();
        first.Dispose();
        Assert.Single(factory.LiveWatchers);
        second.Dispose();
        Assert.Empty(factory.LiveWatchers);

        using var lateToken = context.EnqueueWatchingFile(filePath);
        _ = context.EnqueueWatchingFile(Path.Combine(s_root, "forgotten.cs"));
        context.Dispose();
        context.Dispose();
        lateToken.Dispose();
        using var afterDisposal = context.EnqueueWatchingFile(filePath);
        Assert.Empty(factory.LiveWatchers);
        Assert.Empty(AggregatingFileChangeWatcher.TestAccessor.GetActiveContexts(watcher));
        Assert.Empty(AggregatingFileChangeWatcher.TestAccessor.GetWatchedDirectories(watcher));
    }

    [Fact]
    public void ReleasingChildWatchPreservesSameContextAtFlatParent()
    {
        var factory = new RecordingFactory();
        var watcher = new AggregatingFileChangeWatcher(factory, maxWatcherCount: 10);
        using var context = watcher.CreateContext([]);
        var parentFile = Path.Combine(s_root, "one.cs");
        using var parent = context.EnqueueWatchingFile(parentFile);
        using var child = context.EnqueueWatchingFile(Path.Combine(s_root, "child", "two.cs"));
        child.Dispose();

        var changes = new List<string>();
        context.FileChanged += (_, e) => changes.Add(e.FilePath);
        Assert.Single(factory.LiveWatchers).Raise(new(parentFile, FileChangeKind.Changed));
        Assert.Equal([parentFile], changes);
        Assert.Same(context, Assert.Single(AggregatingFileChangeWatcher.TestAccessor.GetActiveContexts(watcher)));
    }

    [Fact]
    public void SharedWatchNotifiesEachContextOncePerEvent()
    {
        var factory = new RecordingFactory();
        var watcher = new AggregatingFileChangeWatcher(factory, maxWatcherCount: 10);
        using var context = watcher.CreateContext([]);
        var filePath = Path.Combine(s_root, "one.cs");
        using var first = context.EnqueueWatchingFile(filePath);
        using var second = context.EnqueueWatchingFile(Path.Combine(s_root, "two.cs"));
        var changes = new List<string>();
        context.FileChanged += (_, e) => changes.Add(e.FilePath);

        Assert.Single(factory.LiveWatchers).Raise(new(filePath, FileChangeKind.Changed));
        Assert.Equal([filePath], changes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentEnqueueAndDisposalCannotLeakAWatch(bool disposeOwner)
    {
        using var createEntered = new ManualResetEventSlim();
        using var allowCreate = new ManualResetEventSlim();
        using var disposeStarted = new ManualResetEventSlim();
        var factory = new RecordingFactory
        {
            BeforeCreate = (_, _) =>
            {
                createEntered.Set();
                Assert.True(allowCreate.Wait(TimeSpan.FromSeconds(10)));
            },
        };
        var watcher = new AggregatingFileChangeWatcher(factory, maxWatcherCount: 10);
        using var context = watcher.CreateContext([]);
        var enqueue = Task.Run(() => context.EnqueueWatchingFile(Path.Combine(s_root, "one.cs")));
        Assert.True(createEntered.Wait(TimeSpan.FromSeconds(10)));
        var dispose = Task.Run(() =>
        {
            disposeStarted.Set();
            if (disposeOwner)
                watcher.Dispose();
            else
                context.Dispose();
        });
        Assert.True(disposeStarted.Wait(TimeSpan.FromSeconds(10)));
        allowCreate.Set();
        using var token = await enqueue.WaitAsync(TimeSpan.FromSeconds(10));
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(factory.LiveWatchers);
        Assert.Empty(AggregatingFileChangeWatcher.TestAccessor.GetActiveContexts(watcher));
        Assert.Empty(AggregatingFileChangeWatcher.TestAccessor.GetWatchedDirectories(watcher));
    }

    [Fact]
    public void OwnerDisposalReleasesAllContextsWithoutDisposingFactory()
    {
        var factory = new RecordingFactory { SeparateFlatWatches = true };
        using var watcher = new AggregatingFileChangeWatcher(factory, maxWatcherCount: 10);
        using var recursive = watcher.CreateContext([new(s_root, [".cs"])]);
        using var exact = watcher.CreateContext([]);
        using var empty = watcher.CreateContext([]);
        using var lateToken = exact.EnqueueWatchingFile(Path.Combine(s_root, "obj", "project.assets.json"));
        _ = exact.EnqueueWatchingFile(Path.Combine(s_root, "forgotten.json"));
        var capturedWatcher = factory.LiveWatchers.First();
        var changes = new List<string>();
        recursive.FileChanged += (_, e) => changes.Add(e.FilePath);

        watcher.Dispose();
        watcher.Dispose();
        lateToken.Dispose();
        capturedWatcher.Raise(new(Path.Combine(s_root, "one.cs"), FileChangeKind.Changed));
        Assert.Empty(changes);
        Assert.Empty(factory.LiveWatchers);
        Assert.False(factory.Disposed);
        Assert.Empty(AggregatingFileChangeWatcher.TestAccessor.GetActiveContexts(watcher));
        Assert.Empty(AggregatingFileChangeWatcher.TestAccessor.GetWatchedDirectories(watcher));
        Assert.Same(NoOpWatchedFile.Instance, empty.EnqueueWatchingFile(Path.Combine(s_root, "after.cs")));
        Assert.Throws<ObjectDisposedException>(() => watcher.CreateContext([]));
        Assert.Throws<ObjectDisposedException>(() => watcher.CreateContext([new(s_root, [".cs"])]));
    }

    [Fact]
    public async Task OwnerDisposalWaitsForContextConstructionAndClosesItsWatches()
    {
        using var createEntered = new ManualResetEventSlim();
        using var allowCreate = new ManualResetEventSlim();
        using var disposeStarted = new ManualResetEventSlim();
        var factory = new RecordingFactory
        {
            BeforeCreate = (_, _) =>
            {
                createEntered.Set();
                Assert.True(allowCreate.Wait(TimeSpan.FromSeconds(10)));
            },
        };
        using var watcher = new AggregatingFileChangeWatcher(factory, maxWatcherCount: 10);
        var create = Task.Run(() => watcher.CreateContext([new(s_root, [".cs"])]));
        Assert.True(createEntered.Wait(TimeSpan.FromSeconds(10)));
        var dispose = Task.Run(() =>
        {
            disposeStarted.Set();
            watcher.Dispose();
        });
        Assert.True(disposeStarted.Wait(TimeSpan.FromSeconds(10)));
        allowCreate.Set();
        using var context = await create.WaitAsync(TimeSpan.FromSeconds(10));
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(factory.LiveWatchers);
        Assert.Empty(AggregatingFileChangeWatcher.TestAccessor.GetActiveContexts(watcher));
        Assert.Empty(AggregatingFileChangeWatcher.TestAccessor.GetWatchedDirectories(watcher));
        Assert.Same(NoOpWatchedFile.Instance, context.EnqueueWatchingFile(Path.Combine(s_root, "after.json")));
        Assert.Throws<ObjectDisposedException>(() => watcher.CreateContext([]));
    }

    [Fact]
    public void TinyBudgetConsolidatesOnlyWithinAllowedRoots()
    {
        var firstRoot = Path.Combine(s_root, "first");
        var secondRoot = Path.Combine(s_root, "second");
        var factory = new RecordingFactory
        {
            CanConsolidatePath = path => path == firstRoot || path == secondRoot,
        };
        var watcher = new AggregatingFileChangeWatcher(factory, maxWatcherCount: 1);
        using var first = watcher.CreateContext([new(Path.Combine(firstRoot, "a"), [".cs"]), new(Path.Combine(firstRoot, "b"), [".vb"])]);
        using var second = watcher.CreateContext([new(Path.Combine(secondRoot, "a"), [".cs"]), new(Path.Combine(secondRoot, "b"), [".vb"])]);
        AssertEx.SetEqual([firstRoot, secondRoot], factory.LiveWatchers.Select(static w => w.Path));
        Assert.All(factory.LiveWatchers, static w => Assert.True(w.Options.IncludeSubdirectories));
        first.Dispose();
        second.Dispose();
        Assert.Empty(AggregatingFileChangeWatcher.TestAccessor.GetWatchedDirectories(watcher));
    }

    [Fact]
    public void DescriptorCostUpdatesCanTriggerConsolidation()
    {
        var factory = new RecordingFactory
        {
            WatchCount = static options => options.IncludeSubdirectories ? 1 : Math.Max(1, options.Filters.Length),
        };
        var watcher = new AggregatingFileChangeWatcher(factory, maxWatcherCount: 3);
        using var context = watcher.CreateContext([]);
        using var first = context.EnqueueWatchingFile(Path.Combine(s_root, "a", "one.cs"));
        using var second = context.EnqueueWatchingFile(Path.Combine(s_root, "b", "two.cs"));
        Assert.Equal(2, factory.LiveWatchers.Count());

        using var third = context.EnqueueWatchingFile(Path.Combine(s_root, "b", "three.vb"));
        var parent = Assert.Single(factory.LiveWatchers);
        Assert.Equal(s_root, parent.Path);
        Assert.True(parent.Options.IncludeSubdirectories);
        AssertEx.SetEqual(["*.cs", "*.vb"], parent.Options.Filters);
        context.Dispose();
        Assert.Empty(AggregatingFileChangeWatcher.TestAccessor.GetWatchedDirectories(watcher));
    }

    [Fact]
    public void ConsolidationPreservesAllFilesFilter()
    {
        var factory = new RecordingFactory();
        var watcher = new AggregatingFileChangeWatcher(factory, maxWatcherCount: 2);
        using var first = watcher.CreateContext([new(Path.Combine(s_root, "a"), [])]);
        using var second = watcher.CreateContext([new(Path.Combine(s_root, "b"), [".cs"])]);
        Assert.Empty(Assert.Single(factory.LiveWatchers).Options.Filters);
    }

    private sealed class RecordingFactory : IDirectoryWatcherFactory, IDisposable
    {
        private readonly List<RecordingWatcher> _watchers = [];
        private int _updateDepth;

        public StringComparison PathComparison => StringComparison.Ordinal;
        public bool SeparateFlatWatches { get; init; }
        public Func<string, bool> CanConsolidatePath { get; init; } = static _ => true;
        public Func<DirectoryWatchOptions, int> WatchCount { get; init; } = static _ => 1;
        public Action<string, DirectoryWatchOptions>? BeforeCreate { get; set; }
        public Action<RecordingWatcher, DirectoryWatchOptions>? BeforeUpdate { get; set; }
        public List<(string operation, string path)> Operations { get; } = [];
        public IEnumerable<RecordingWatcher> LiveWatchers => _watchers.Where(static w => !w.Disposed);
        public bool Disposed { get; private set; }

        public bool CanWatchDirectory(string path) => true;
        public bool CanConsolidate(string path) => CanConsolidatePath(path);
        public int GetWatchCount(DirectoryWatchOptions options) => WatchCount(options);
        public void Dispose() => Disposed = true;

        public IDisposable BeginUpdate()
        {
            Assert.Equal(0, _updateDepth++);
            return new UpdateScope(this);
        }

        public IDirectoryWatcher Create(string path, DirectoryWatchOptions options, Action<FileChangedEventArgs> onChanged)
        {
            Assert.Equal(1, _updateDepth);
            BeforeCreate?.Invoke(path, options);
            var watcher = new RecordingWatcher(this, path, options, onChanged);
            _watchers.Add(watcher);
            Operations.Add(("Create", path));
            return watcher;
        }

        public sealed class RecordingWatcher(RecordingFactory factory, string path, DirectoryWatchOptions options, Action<FileChangedEventArgs> onChanged) : IDirectoryWatcher
        {
            public string Path { get; } = path;
            public DirectoryWatchOptions Options { get; private set; } = options;
            public bool Disposed { get; private set; }

            public void Update(DirectoryWatchOptions options)
            {
                Assert.Equal(1, factory._updateDepth);
                Assert.False(Disposed);
                factory.BeforeUpdate?.Invoke(this, options);
                Options = options;
                factory.Operations.Add(("Update", Path));
            }

            public void Raise(FileChangedEventArgs e) => onChanged(e);

            public void Dispose()
            {
                Assert.Equal(1, factory._updateDepth);
                Assert.False(Disposed);
                Disposed = true;
                factory.Operations.Add(("Dispose", Path));
            }
        }

        private sealed class UpdateScope(RecordingFactory factory) : IDisposable
        {
            public void Dispose() => Assert.Equal(1, factory._updateDepth--);
        }
    }
}
