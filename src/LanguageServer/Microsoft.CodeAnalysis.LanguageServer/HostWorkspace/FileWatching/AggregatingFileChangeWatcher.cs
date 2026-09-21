// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.ProjectSystem;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.LanguageServer.HostWorkspace.FileWatching;

/// <summary>
/// Shares directory watches across contexts and consolidates them to respect the backend's soft budget.
/// </summary>
internal sealed partial class AggregatingFileChangeWatcher : IFileChangeWatcher, IDisposable
{
    private readonly IDirectoryWatcherFactory _factory;
    private readonly StringComparer _pathComparer;

    /// <summary>
    /// Protects both trees and their backend update scopes. Event dispatch snapshots contexts under this
    /// gate, then releases it before taking a context's gate or invoking consumers.
    /// </summary>
    private readonly object _gate = new();
    private readonly object _disposeGate = new();
    private readonly Dictionary<string, DirectoryNode> _roots;
    private readonly Dictionary<string, DirectoryNode> _flatRoots;
    private readonly HashSet<FileChangeContext> _contexts = [];
    private readonly int _maxWatcherCount;
    private int _currentWatcherCount;
    private bool _disposed;

    /// <param name="factory">The backend and its path, counting, and consolidation policies.</param>
    /// <param name="maxWatcherCount">A soft limit; separate roots and protected flat watches may prevent consolidation.</param>
    public AggregatingFileChangeWatcher(IDirectoryWatcherFactory factory, int maxWatcherCount)
    {
        _factory = factory;
        _pathComparer = StringComparer.FromComparison(factory.PathComparison);
        _roots = new(_pathComparer);
        _flatRoots = new(_pathComparer);
        _maxWatcherCount = maxWatcherCount;
    }

    public IFileChangeContext CreateContext(ImmutableArray<WatchedDirectory> watchedDirectories)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // The context is not published until construction completes, so shutdown cannot miss its watches.
            var context = new FileChangeContext(this, watchedDirectories);
            _contexts.Add(context);
            return context;
        }
    }

    /// <summary>
    /// Releases logical watches without disposing the backend. The caller can then drain the backend's
    /// asynchronous cleanup before disposing it.
    /// </summary>
    public void Dispose()
    {
        // Concurrent callers must also wait for the context releases before draining the backend.
        lock (_disposeGate)
        {
            ImmutableArray<FileChangeContext> contexts;
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                contexts = [.. _contexts];
                _contexts.Clear();
            }

            // Do not take an existing context's gate while holding the tree gate.
            foreach (var context in contexts)
                context.Dispose();
        }
    }

    private void OnContextDisposed(FileChangeContext context)
    {
        lock (_gate)
            _contexts.Remove(context);
    }

    private IDisposable AcquireDirectoryWatch(WatchedDirectory watchedDirectory, FileChangeContext fileChangeContext, bool includeSubdirectories)
    {
        var watchedDirectoryPath = watchedDirectory.Path.AsSpan();
        var roots = _factory.SeparateFlatWatches && !includeSubdirectories ? _flatRoots : _roots;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var update = _factory.BeginUpdate();
            var root = Path.GetPathRoot(watchedDirectoryPath);

            if (!roots.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(root, out var node))
            {
                var rootString = root.ToString();
                node = new DirectoryNode(rootString, parent: null, _pathComparer);
                roots.Add(rootString, node);
            }

            DirectoryNode? coveringNode = null;
            var pathRelativeToRoot = watchedDirectoryPath[root.Length..];
            foreach (var pathComponentRange in pathRelativeToRoot.SplitAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]))
            {
                var pathComponent = pathRelativeToRoot[pathComponentRange];
                if (pathComponent.Length == 0)
                    continue;

                if (node.Watcher is not null && node.Options.IncludeSubdirectories)
                    coveringNode ??= node;

                if (!node.Children.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(pathComponent, out var childNode))
                {
                    childNode = new DirectoryNode(watchedDirectoryPath[..(root.Length + pathComponentRange.End.Value)].ToString(), node, _pathComparer);
                    node.Children.Add(pathComponent.ToString(), childNode);
                }

                node = childNode;
            }

            // Recording ownership before backend changes lets the same release path roll back a failed acquisition.
            for (var current = node; current is not null; current = current.Parent)
                current.ActiveContextsRecursiveCount++;

            var contextAdded = false;
            try
            {
                var watchNode = coveringNode ?? node;
                while (watchNode.Watcher is null && !_factory.CanWatchDirectory(watchNode.Path))
                {
                    if (watchNode.Parent is null)
                        throw new DirectoryNotFoundException(watchedDirectory.Path);

                    includeSubdirectories = true;
                    watchNode = watchNode.Parent;
                }

                var filters = watchedDirectory.ExtensionFilters.SelectAsArray(static extension => '*' + extension);
                var options = new DirectoryWatchOptions(filters, includeSubdirectories);
                if (watchNode.Watcher is not null)
                    options = ExpandOptionsToCover(watchNode.Options, options);

                var ownWatchCount = watchNode.Watcher is null ? 0 : _factory.GetWatchCount(watchNode.Options);
                if (options.IncludeSubdirectories && watchNode.ActiveWatchersRecursiveCount > ownWatchCount)
                {
                    Consolidate_NoLock(watchNode, options);
                }
                else
                {
                    SetWatcherOptions_NoLock(watchNode, options);
                }

                watchNode.ActiveContexts = watchNode.ActiveContexts.Add(fileChangeContext);
                contextAdded = true;
                FindBestNodeToConsolidateAndConsolidateIt_NoLock();
                return new DirectoryWatch(this, roots, node, fileChangeContext);
            }
            catch
            {
                ReleaseWatch_NoLock(roots, node, fileChangeContext, removeContext: contextAdded);
                throw;
            }
        }
    }

    private void ReleaseWatch(Dictionary<string, DirectoryNode> roots, DirectoryNode node, FileChangeContext fileChangeContext)
    {
        lock (_gate)
        {
            using var update = _factory.BeginUpdate();
            ReleaseWatch_NoLock(roots, node, fileChangeContext);
        }
    }

    private void ReleaseWatch_NoLock(Dictionary<string, DirectoryNode> roots, DirectoryNode node, FileChangeContext fileChangeContext, bool removeContext = true)
    {
        Contract.ThrowIfFalse(Monitor.IsEntered(_gate));

        for (var current = node; current is not null; current = current.Parent)
        {
            Contract.ThrowIfFalse(current.ActiveContextsRecursiveCount > 0);
            current.ActiveContextsRecursiveCount--;
            if (removeContext && current.Watcher is not null)
            {
                current.ActiveContexts = current.ActiveContexts.Remove(fileChangeContext);
                removeContext = false;
            }

            if (current.ActiveContextsRecursiveCount != 0)
                continue;

            Contract.ThrowIfFalse(current.ActiveContexts.IsEmpty);
            Contract.ThrowIfFalse(current.Children.Count == 0, "If we have no active contexts, we should have no children as well.");

            if (current.Watcher is not null)
                DisposeWatcher_NoLock(current);

            if (current.Parent is not null)
            {
                foreach (var (key, value) in current.Parent.Children)
                {
                    if (ReferenceEquals(value, current))
                    {
                        current.Parent.Children.Remove(key);
                        break;
                    }
                }
            }
            else
            {
                Contract.ThrowIfFalse(roots.Remove(current.Path));
            }
        }
    }

    private void FindBestNodeToConsolidateAndConsolidateIt_NoLock()
    {
        Contract.ThrowIfFalse(Monitor.IsEntered(_gate));

        // Preserve native's consolidation threshold. Descriptor costs can require more than one merge.
        while (_currentWatcherCount >= _maxWatcherCount)
        {
            DirectoryNode? nodeToConsolidate = null;
            foreach (var root in _roots.Values.OrderByDescending(static r => r.ActiveWatchersRecursiveCount))
            {
                nodeToConsolidate = FindNodeToConsolidate(root);
                if (nodeToConsolidate is not null)
                    break;
            }

            if (nodeToConsolidate is null)
                return;

            var options = GetConsolidatedOptions(nodeToConsolidate);
            Consolidate_NoLock(nodeToConsolidate, options);

            if (_currentWatcherCount <= _maxWatcherCount)
                return;
        }

        DirectoryNode? FindNodeToConsolidate(DirectoryNode node)
        {
            if (node.ActiveWatchersRecursiveCount <= 1)
                return null;

            foreach (var child in node.Children.Values.OrderByDescending(static c => c.ActiveWatchersRecursiveCount))
            {
                if (FindNodeToConsolidate(child) is { } candidate)
                    return candidate;
            }

            // A single watch with several backend descriptors cannot be improved by moving it up.
            var ownCount = node.Watcher is null ? 0 : _factory.GetWatchCount(node.Options);
            if (node.ActiveWatchersRecursiveCount == ownCount ||
                !_factory.CanConsolidate(node.Path) ||
                !_factory.CanWatchDirectory(node.Path))
            {
                return null;
            }

            return _factory.GetWatchCount(GetConsolidatedOptions(node)) < node.ActiveWatchersRecursiveCount ? node : null;
        }
    }

    private static DirectoryWatchOptions GetConsolidatedOptions(DirectoryNode node)
    {
        DirectoryWatchOptions? options = node.Watcher is null ? null : node.Options;
        CollectChildOptions(node, ref options);
        Contract.ThrowIfNull(options);
        return options.Value with { IncludeSubdirectories = true };
    }

    private static void CollectChildOptions(DirectoryNode node, ref DirectoryWatchOptions? options)
    {
        foreach (var child in node.Children.Values)
        {
            if (child.Watcher is not null)
                options = options is { } existing ? ExpandOptionsToCover(existing, child.Options) : child.Options;

            if (child.ActiveWatchersRecursiveCount > 0)
                CollectChildOptions(child, ref options);
        }
    }

    private void Consolidate_NoLock(DirectoryNode node, DirectoryWatchOptions options)
    {
        var activeContexts = node.ActiveContexts.ToBuilder();
        DirectoryWatchOptions? combinedOptions = options;
        CollectChildOptions(node, ref combinedOptions);
        CollectChildContexts(node, activeContexts);
        Contract.ThrowIfNull(combinedOptions);

        // Backend configuration must succeed before either coverage or ownership is removed from children.
        SetWatcherOptions_NoLock(node, combinedOptions.Value with { IncludeSubdirectories = true });
        node.ActiveContexts = activeContexts.ToImmutable();
        RemoveWatchersFromChildrenForConsolidation_NoLock(node, coveringNode: node);

        static void CollectChildContexts(DirectoryNode node, ImmutableArray<FileChangeContext>.Builder contexts)
        {
            foreach (var child in node.Children.Values)
            {
                contexts.AddRange(child.ActiveContexts);
                if (child.ActiveWatchersRecursiveCount > 0)
                    CollectChildContexts(child, contexts);
            }
        }
    }

    private void RemoveWatchersFromChildrenForConsolidation_NoLock(DirectoryNode node, DirectoryNode coveringNode)
    {
        foreach (var child in node.Children.Values)
        {
            if (child.Watcher is not null)
            {
                // A backend may already have captured this child's callback. Keep that delivery path valid
                // after its contexts have moved, including callbacks arriving after backend disposal.
                child.ForwardingNode = coveringNode;
                DisposeWatcher_NoLock(child);
                child.ActiveContexts = [];
            }

            if (child.ActiveWatchersRecursiveCount > 0)
                RemoveWatchersFromChildrenForConsolidation_NoLock(child, coveringNode);
        }
    }

    private void SetWatcherOptions_NoLock(DirectoryNode node, DirectoryWatchOptions options)
    {
        var newCount = _factory.GetWatchCount(options);
        var oldCount = 0;
        if (node.Watcher is null)
        {
            node.Watcher = _factory.Create(node.Path, options, e => OnFileChanged(node, e));
        }
        else
        {
            if (node.Options.HasSameConfiguration(options))
                return;

            oldCount = _factory.GetWatchCount(node.Options);
            node.Watcher.Update(options);
        }

        node.Options = options;
        UpdateWatcherCount_NoLock(node, newCount - oldCount);
    }

    private void DisposeWatcher_NoLock(DirectoryNode node)
    {
        Contract.ThrowIfNull(node.Watcher);
        node.Watcher.Dispose();
        node.Watcher = null;
        UpdateWatcherCount_NoLock(node, -_factory.GetWatchCount(node.Options));
    }

    private void UpdateWatcherCount_NoLock(DirectoryNode node, int delta)
    {
        _currentWatcherCount += delta;
        for (var current = node; current is not null; current = current.Parent)
        {
            current.ActiveWatchersRecursiveCount += delta;
            Contract.ThrowIfTrue(current.ActiveWatchersRecursiveCount < 0);
        }
    }

    private void OnFileChanged(DirectoryNode node, FileChangedEventArgs e)
    {
        // Recording backends and native watcher setup can notify synchronously. Never call a consumer
        // from inside a topology update, which may itself be acquiring a watch under a context's gate.
        if (Monitor.IsEntered(_gate))
        {
            ThreadPool.QueueUserWorkItem(static state => state.owner.OnFileChanged(state.node, state.e), (owner: this, node, e), preferLocal: false);
            return;
        }

        ImmutableArray<FileChangeContext> activeContexts;
        lock (_gate)
        {
            while (node.ForwardingNode is { } forwardingNode)
                node = forwardingNode;

            activeContexts = node.ActiveContexts;
        }

        using var _ = PooledHashSet<FileChangeContext>.GetInstance(out var notifiedContexts);
        foreach (var activeContext in activeContexts)
        {
            if (notifiedContexts.Add(activeContext))
                activeContext.OnFileChanged(e);
        }
    }

    /// <summary>
    /// A directory in either tree. Context references move towards a covering watcher during consolidation,
    /// while recursive counts retain the original acquisition locations for release and pruning.
    /// </summary>
    private sealed class DirectoryNode(string path, DirectoryNode? parent, StringComparer comparer)
    {
        public string Path { get; } = path;
        public DirectoryNode? Parent { get; } = parent;
        public Dictionary<string, DirectoryNode> Children { get; } = new(comparer);
        public IDirectoryWatcher? Watcher { get; set; }
        public DirectoryWatchOptions Options { get; set; }
        public DirectoryNode? ForwardingNode { get; set; }
        public ImmutableArray<FileChangeContext> ActiveContexts { get; set; } = [];
        public int ActiveContextsRecursiveCount { get; set; }
        public int ActiveWatchersRecursiveCount { get; set; }
    }

    private sealed class DirectoryWatch(
        AggregatingFileChangeWatcher owner,
        Dictionary<string, DirectoryNode> roots,
        DirectoryNode node,
        FileChangeContext fileChangeContext) : IDisposable
    {
        private AggregatingFileChangeWatcher? _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, value: null) is { } owner)
                owner.ReleaseWatch(roots, node, fileChangeContext);
        }
    }

    private static DirectoryWatchOptions ExpandOptionsToCover(DirectoryWatchOptions options, DirectoryWatchOptions newOptions)
    {
        var filters = options.Filters.ToList();
        ExpandFiltersToCover(filters, newOptions.Filters);
        return new([.. filters], options.IncludeSubdirectories || newOptions.IncludeSubdirectories);
    }

    /// <summary>
    /// Merges filters, treating an empty collection as all files.
    /// </summary>
    private static void ExpandFiltersToCover(ICollection<string> watcherFilters, IEnumerable<string> newFilters)
    {
        if (watcherFilters.Count == 0)
            return;

        var hadAFilter = false;
        foreach (var newFilter in newFilters)
        {
            hadAFilter = true;
            if (!watcherFilters.Contains(newFilter))
                watcherFilters.Add(newFilter);
        }

        if (!hadAFilter)
            watcherFilters.Clear();
    }

    internal static class TestAccessor
    {
        public static IEnumerable<(string path, ImmutableArray<string> filters, bool includeSubdirectories)> GetWatchedDirectories(AggregatingFileChangeWatcher watcher)
        {
            lock (watcher._gate)
            {
                var paths = new List<(string path, ImmutableArray<string> filters, bool includeSubdirectories)>();
                var currentWatcherCountPerRoots = 0;
                var actualWatcherCount = 0;
                foreach (var root in watcher._roots.Values.Concat(watcher._flatRoots.Values))
                {
                    currentWatcherCountPerRoots += root.ActiveWatchersRecursiveCount;
                    AddWatchedDirectoryPaths(root);
                }

                Contract.ThrowIfTrue(actualWatcherCount != watcher._currentWatcherCount, "The total watcher count is out of sync.");
                Contract.ThrowIfTrue(currentWatcherCountPerRoots != watcher._currentWatcherCount, "The root watcher counts are out of sync.");
                return paths;

                void AddWatchedDirectoryPaths(DirectoryNode node)
                {
                    if (node.Watcher is not null)
                    {
                        paths.Add((node.Path, node.Options.Filters, node.Options.IncludeSubdirectories));
                        actualWatcherCount += watcher._factory.GetWatchCount(node.Options);
                    }

                    foreach (var child in node.Children.Values)
                        AddWatchedDirectoryPaths(child);
                }
            }
        }

        public static IReadOnlyCollection<IFileChangeContext> GetActiveContexts(AggregatingFileChangeWatcher watcher)
        {
            lock (watcher._gate)
            {
                var contexts = new HashSet<IFileChangeContext>(ReferenceEqualityComparer.Instance);
                foreach (var root in watcher._roots.Values.Concat(watcher._flatRoots.Values))
                    AddActiveContexts(root);

                return contexts;

                void AddActiveContexts(DirectoryNode node)
                {
                    foreach (var context in node.ActiveContexts)
                        contexts.Add(context);

                    foreach (var child in node.Children.Values)
                        AddActiveContexts(child);
                }
            }
        }
    }
}
