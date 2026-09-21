// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.ProjectSystem;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Threading;
using Microsoft.Extensions.Logging;
using Roslyn.LanguageServer.Protocol;
using Roslyn.Utilities;
using StreamJsonRpc;
using FileSystemWatcher = Roslyn.LanguageServer.Protocol.FileSystemWatcher;
using RemoteInvocationException = StreamJsonRpc.RemoteInvocationException;

namespace Microsoft.CodeAnalysis.LanguageServer.HostWorkspace.FileWatching;

internal sealed class LspDirectoryWatcherFactory : IDirectoryWatcherFactory, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IClientLanguageServerManager _client;
    private readonly LspDidChangeWatchedFilesHandler _handler;
    private readonly ImmutableArray<string> _workspacePaths;
    private readonly ILogger _logger;
    private readonly AsyncBatchingWorkQueue _workQueue;
    private readonly Dictionary<LspDirectoryWatcher, DirectoryWatchOptions> _desired = [];

    // Only accessed by the serial work queue. Keep uncertain/failed registrations until removal is acknowledged.
    private readonly Dictionary<string, RegisteredWatch> _registered = [];
    private int _updateDepth;
    private bool _changed;
    private bool _disposed;
    private Task? _disposeTask;

    public LspDirectoryWatcherFactory(
        IClientLanguageServerManager client,
        LspDidChangeWatchedFilesHandler handler,
        IAsynchronousOperationListener listener,
        ImmutableArray<string> workspacePaths,
        ILogger logger)
    {
        _client = client;
        _handler = handler;
        _workspacePaths = workspacePaths.SelectAsArray(static path => Path.TrimEndingDirectorySeparator(path));
        _logger = logger;
        _workQueue = new AsyncBatchingWorkQueue(TimeSpan.FromMilliseconds(100), ReconcileAsync, listener);
        _handler.NotificationRaised += OnNotification;
    }

    public StringComparison PathComparison
        => RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public bool SeparateFlatWatches => true;

    public bool CanWatchDirectory(string path) => true;

    public bool CanConsolidate(string path)
    {
        foreach (var workspacePath in _workspacePaths)
        {
            // Never widen a requested subtree to an entire filesystem/share, even if it is a workspace folder.
            if (!Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!).Equals(Path.TrimEndingDirectorySeparator(path), PathComparison) &&
                IsWithinDirectory(path, workspacePath))
            {
                return true;
            }
        }

        // Without a known boundary, keep individual directory watches instead of guessing an ancestor.
        return false;
    }

    public int GetWatchCount(DirectoryWatchOptions options) => GetPatterns(options).Length;

    public IDisposable BeginUpdate()
    {
        Monitor.Enter(_gate);
        _updateDepth++;
        return new UpdateScope(this);
    }

    private void EndUpdate()
    {
        try
        {
            _updateDepth--;
            if (_updateDepth == 0 && _changed && !_disposed)
            {
                _changed = false;
                _workQueue.AddWork();
            }
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    public IDirectoryWatcher Create(string path, DirectoryWatchOptions options, Action<FileChangedEventArgs> onChanged)
    {
        using var _ = BeginUpdate();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var watcher = new LspDirectoryWatcher(this, path, onChanged);
        _desired.Add(watcher, options);
        _changed = true;
        return watcher;
    }

    private void Update(LspDirectoryWatcher watcher, DirectoryWatchOptions options)
    {
        using var _ = BeginUpdate();
        if (_desired.TryGetValue(watcher, out var previous) && !previous.HasSameConfiguration(options))
        {
            _desired[watcher] = options;
            _changed = true;
        }
    }

    private void Remove(LspDirectoryWatcher watcher)
    {
        using var _ = BeginUpdate();
        _changed |= _desired.Remove(watcher);
    }

    private async ValueTask ReconcileAsync(CancellationToken cancellationToken)
    {
        Dictionary<LspDirectoryWatcher, DirectoryWatchOptions> desired;
        lock (_gate)
            desired = new(_desired);

        var retainedIds = new HashSet<string>();
        foreach (var (id, registration) in _registered)
        {
            if (registration.Acknowledged &&
                desired.TryGetValue(registration.Watcher, out var options) &&
                options.HasSameConfiguration(registration.Options))
            {
                retainedIds.Add(id);
                desired.Remove(registration.Watcher);
            }
        }

        // Register all replacement coverage before removing any old coverage, including updates of the same handle.
        foreach (var (watcher, options) in desired)
        {
            var id = Guid.NewGuid().ToString();
            var registration = new RegisteredWatch(watcher, options);
            _registered.Add(id, registration);
            var patterns = GetPatterns(options);
            try
            {
                await _client.SendRequestAsync(
                    "client/registerCapability",
                    new RegistrationParams
                    {
                        Registrations =
                        [
                            new Registration
                            {
                                Id = id,
                                Method = Methods.WorkspaceDidChangeWatchedFilesName,
                                RegisterOptions = new DidChangeWatchedFilesRegistrationOptions
                                {
                                    Watchers = patterns.SelectAsArray(pattern => new FileSystemWatcher
                                    {
                                        GlobPattern = new RelativePattern
                                        {
                                            BaseUri = ProtocolConversions.CreateAbsoluteDocumentUri(watcher.Path),
                                            Pattern = pattern,
                                        },
                                    }).ToArray(),
                                },
                            },
                        ],
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (RemoteInvocationException ex)
            {
                _logger.LogError(ex, "File watch registration failed; retaining previous coverage for {Path}", watcher.Path);
                return;
            }
            catch (ConnectionLostException ex)
            {
                ReportConnectionLoss(ex);
                return;
            }

            registration.Acknowledged = true;
            retainedIds.Add(id);
        }

        foreach (var id in _registered.Keys.ToArray())
        {
            if (retainedIds.Contains(id))
                continue;

            try
            {
                await _client.SendRequestAsync(
                    "client/unregisterCapability",
                    new UnregistrationParams
                    {
                        Unregistrations =
                        [
                            new Unregistration { Id = id, Method = Methods.WorkspaceDidChangeWatchedFilesName },
                        ],
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (RemoteInvocationException ex)
            {
                _logger.LogError(ex, "File watch unregistration failed for {RegistrationId}; keeping it pending for cleanup", id);
                continue;
            }
            catch (ConnectionLostException ex)
            {
                ReportConnectionLoss(ex);
                return;
            }

            _registered.Remove(id);
        }

        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace(
                "File watching: {Registrations} client registrations, {Descriptors} watcher descriptors",
                _registered.Count,
                _registered.Values.Sum(registration => GetWatchCount(registration.Options)));
        }
    }

    private void ReportConnectionLoss(ConnectionLostException exception)
    {
        bool disposed;
        lock (_gate)
            disposed = _disposed;

        if (!disposed)
            _logger.LogError(exception, "Client disconnected while updating file watches");
    }

    private void OnNotification(object? sender, DidChangeWatchedFilesParams notification)
    {
        KeyValuePair<LspDirectoryWatcher, DirectoryWatchOptions>[] watches;
        lock (_gate)
            watches = [.. _desired];

        foreach (var change in notification.Changes)
        {
            var path = change.Uri.GetRequiredParsedUri().FsPath;
            var kind = change.FileChangeType switch
            {
                FileChangeType.Created => FileChangeKind.Created,
                FileChangeType.Changed => FileChangeKind.Changed,
                FileChangeType.Deleted => FileChangeKind.Deleted,
                _ => throw ExceptionUtilities.UnexpectedValue(change.FileChangeType),
            };
            var args = new FileChangedEventArgs(path, kind);

            // Route via desired aggregates: old registrations may still be covering a newly consolidated parent.
            foreach (var (watcher, options) in watches)
            {
                if (Matches(watcher.Path, options, path))
                    watcher.OnChanged(args);
            }
        }
    }

    private bool Matches(string directory, DirectoryWatchOptions options, string path)
    {
        if (options.IncludeSubdirectories
                ? !IsWithinDirectory(path, directory)
                : !directory.Equals(Path.GetDirectoryName(path), PathComparison))
        {
            return false;
        }

        if (options.Filters.IsEmpty)
            return true;

        foreach (var filter in options.Filters)
        {
            // Aggregation produces extension filters ("*.cs") or an all-files filter, not arbitrary globs.
            if (filter == "*" || path.EndsWith(filter[1..], PathComparison))
                return true;
        }

        return false;
    }

    private bool IsWithinDirectory(string path, string directory)
        => path.Equals(directory, PathComparison) ||
           (path.Length > directory.Length &&
            path.StartsWith(directory, PathComparison) &&
            (PathUtilities.IsDirectorySeparator(directory[^1]) || PathUtilities.IsDirectorySeparator(path[directory.Length])));

    internal static ImmutableArray<string> GetPatterns(DirectoryWatchOptions options)
    {
        const int maximumPatternLength = 4096;
        var prefix = options.IncludeSubdirectories ? "**/" : "";
        if (options.Filters.IsEmpty || options.Filters.Contains("*"))
            return [prefix + "*"];

        using var _ = ArrayBuilder<string>.GetInstance(out var patterns);
        var alternatives = new List<string>();
        var length = prefix.Length + 2;
        foreach (var filter in options.Filters)
        {
            Contract.ThrowIfFalse(filter.StartsWith('*'));
            var escaped = "*" + EscapeGlobLiteral(filter[1..]);
            if (prefix.Length + escaped.Length > maximumPatternLength)
                throw new ArgumentException("A file watch filter exceeds the maximum LSP pattern length.", nameof(options));

            if (alternatives.Count > 0 && length + escaped.Length + 1 > maximumPatternLength)
            {
                AddPattern();
                length = prefix.Length + 2;
            }

            alternatives.Add(escaped);
            length += escaped.Length + 1;
        }

        AddPattern();
        return patterns.ToImmutableAndClear();

        void AddPattern()
        {
            patterns.Add(prefix + (alternatives.Count == 1 ? alternatives[0] : "{" + string.Join(',', alternatives) + "}"));
            alternatives.Clear();
        }
    }

    private static string EscapeGlobLiteral(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value)
        {
            if (character is '*' or '?' or '[' or ']' or '{' or '}' or ',')
                builder.Append('[').Append(character).Append(']');
            else
                builder.Append(character);
        }

        return builder.ToString();
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _desired.Clear();
                _handler.NotificationRaised -= OnNotification;
                _workQueue.AddWork();
                _disposeTask = FinishDisposalAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task FinishDisposalAsync()
    {
        try
        {
            await _workQueue.WaitUntilCurrentBatchCompletesAsync().ConfigureAwait(false);
        }
        finally
        {
            _workQueue.Dispose();
            _registered.Clear();
        }
    }

    private sealed class UpdateScope(LspDirectoryWatcherFactory factory) : IDisposable
    {
        public void Dispose() => factory.EndUpdate();
    }

    private sealed class RegisteredWatch(LspDirectoryWatcher watcher, DirectoryWatchOptions options)
    {
        public LspDirectoryWatcher Watcher { get; } = watcher;
        public DirectoryWatchOptions Options { get; } = options;
        public bool Acknowledged { get; set; }
    }

    private sealed class LspDirectoryWatcher(
        LspDirectoryWatcherFactory factory, string path, Action<FileChangedEventArgs> onChanged) : IDirectoryWatcher
    {
        public string Path { get; } = System.IO.Path.TrimEndingDirectorySeparator(path);
        public Action<FileChangedEventArgs> OnChanged { get; } = onChanged;
        public void Update(DirectoryWatchOptions options) => factory.Update(this, options);
        public void Dispose() => factory.Remove(this);
    }
}
