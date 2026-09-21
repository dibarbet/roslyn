// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis.ProjectSystem;

namespace Microsoft.CodeAnalysis.LanguageServer.HostWorkspace.FileWatching;

internal sealed class NativeDirectoryWatcherFactory : IDirectoryWatcherFactory
{
    public StringComparison PathComparison => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public bool SeparateFlatWatches => false;
    public bool CanWatchDirectory(string path) => Directory.Exists(path);
    public bool CanConsolidate(string path) => true;
    public int GetWatchCount(DirectoryWatchOptions options) => 1;
    public IDisposable BeginUpdate() => NoOpWatchedFile.Instance;

    public IDirectoryWatcher Create(string path, DirectoryWatchOptions options, Action<FileChangedEventArgs> onChanged)
        => new NativeDirectoryWatcher(path, options, onChanged);

    private sealed class NativeDirectoryWatcher : IDirectoryWatcher
    {
        private readonly string _path;
        private readonly Action<FileChangedEventArgs> _onChanged;
        private FileSystemWatcher _watcher;

        public NativeDirectoryWatcher(string path, DirectoryWatchOptions options, Action<FileChangedEventArgs> onChanged)
        {
            _path = path;
            _onChanged = onChanged;
            _watcher = CreateWatcher(options);
        }

        public void Update(DirectoryWatchOptions options)
        {
            // Establish the replacement before touching the old watch, including when configuration fails.
            var replacement = CreateWatcher(options);
            var oldWatcher = _watcher;
            _watcher = replacement;
            oldWatcher.Dispose();
        }

        private FileSystemWatcher CreateWatcher(DirectoryWatchOptions options)
        {
            var watcher = new FileSystemWatcher(_path);
            try
            {
                watcher.Filters.AddRange(options.Filters);
                watcher.IncludeSubdirectories = options.IncludeSubdirectories;
                watcher.Created += OnFileSystemEvent;
                watcher.Changed += OnFileSystemEvent;
                watcher.Renamed += OnFileSystemEvent;
                watcher.Deleted += OnFileSystemEvent;
                watcher.EnableRaisingEvents = true;
                return watcher;
            }
            catch
            {
                watcher.Dispose();
                throw;
            }
        }

        private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
        {
            var changeKind = e.ChangeType switch
            {
                WatcherChangeTypes.Created or WatcherChangeTypes.Renamed => FileChangeKind.Created,
                WatcherChangeTypes.Deleted => FileChangeKind.Deleted,
                _ => FileChangeKind.Changed,
            };

            _onChanged(new(e.FullPath, changeKind));

            if (e is RenamedEventArgs renamed && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                _onChanged(new(renamed.OldFullPath, FileChangeKind.Deleted));
        }

        public void Dispose() => _watcher.Dispose();
    }
}
