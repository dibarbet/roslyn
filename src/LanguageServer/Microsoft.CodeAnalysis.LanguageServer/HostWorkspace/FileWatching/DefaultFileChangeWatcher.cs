// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.ProjectSystem;

namespace Microsoft.CodeAnalysis.LanguageServer.HostWorkspace.FileWatching;

/// <summary>
/// Shares native directory watches when the client cannot provide file watching.
/// </summary>
internal sealed class DefaultFileChangeWatcher(int maxWatcherCount = 1000) : IFileChangeWatcher
{
    private readonly AggregatingFileChangeWatcher _watcher = new(new NativeDirectoryWatcherFactory(), maxWatcherCount);

    public IFileChangeContext CreateContext(ImmutableArray<WatchedDirectory> watchedDirectories)
        => _watcher.CreateContext(watchedDirectories);

    internal static class TestAccessor
    {
        public static IEnumerable<(string path, ImmutableArray<string> filters, bool includeSubdirectories)> GetWatchedDirectories(DefaultFileChangeWatcher watcher)
            => AggregatingFileChangeWatcher.TestAccessor.GetWatchedDirectories(watcher._watcher);

        public static IReadOnlyCollection<IFileChangeContext> GetActiveContexts(DefaultFileChangeWatcher watcher)
            => AggregatingFileChangeWatcher.TestAccessor.GetActiveContexts(watcher._watcher);
    }
}
