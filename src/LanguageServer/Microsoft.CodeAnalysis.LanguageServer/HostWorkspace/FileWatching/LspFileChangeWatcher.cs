// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.ProjectSystem;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CommonLanguageServerProtocol.Framework;
using Microsoft.Extensions.Logging;

namespace Microsoft.CodeAnalysis.LanguageServer.HostWorkspace.FileWatching;

/// <summary>
/// Uses the same aggregation and context ownership as the native watcher, with a connection-local LSP backend.
/// </summary>
internal sealed class LspFileChangeWatcher : IFileChangeWatcher, IAsyncDisposable
{
    private readonly LspDirectoryWatcherFactory _factory;
    private readonly AggregatingFileChangeWatcher _watcher;

    private LspFileChangeWatcher(ILspServices services, IAsynchronousOperationListenerProvider listenerProvider)
    {
        _factory = new LspDirectoryWatcherFactory(
            services.GetRequiredService<IClientLanguageServerManager>(),
            services.GetRequiredService<LspDidChangeWatchedFilesHandler>(),
            listenerProvider.GetListener(FeatureAttribute.Workspace),
            services.GetRequiredService<IInitializeManager>().GetRequiredWorkspaceFolderPaths(),
            services.GetRequiredService<ILoggerFactory>().CreateLogger<LspFileChangeWatcher>());
        _watcher = new AggregatingFileChangeWatcher(_factory, maxWatcherCount: 64);
    }

    public static bool TryCreate(ILspServices services, IAsynchronousOperationListenerProvider listenerProvider, [NotNullWhen(true)] out LspFileChangeWatcher? watcher)
    {
        var capabilities = services.GetRequiredService<IInitializeManager>().GetClientCapabilities().Workspace?.DidChangeWatchedFiles;

        // String globs do not establish watches outside the workspace in clients such as VS Code.
        if (capabilities is { DynamicRegistration: true, RelativePatternSupport: true })
        {
            watcher = new LspFileChangeWatcher(services, listenerProvider);
            return true;
        }

        watcher = null;
        return false;
    }

    public IFileChangeContext CreateContext(ImmutableArray<WatchedDirectory> watchedDirectories)
        => _watcher.CreateContext(watchedDirectories);

    public ValueTask DisposeAsync()
    {
        _watcher.Dispose();
        return _factory.DisposeAsync();
    }
}
