// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Composition;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.LanguageServer.HostWorkspace;
using Microsoft.CodeAnalysis.LanguageServer.HostWorkspace.FileWatching;

namespace Microsoft.CodeAnalysis.LanguageServer.LanguageServer;

/// <summary>
/// Manages the association between LSP server instances and workspaces.
/// In the current single-server model, this is a simple coordinator that:
/// <list type="number">
/// <item>Creates the workspace (via <see cref="LanguageServerWorkspaceFactory"/>) when the server registers.</item>
/// <item>Stores the active <see cref="ILspClientSink"/> for workspace-scoped components to use.</item>
/// </list>
/// </summary>
[Export(typeof(SharedWorkspaceManager)), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class SharedWorkspaceManager(Lazy<LanguageServerWorkspaceFactory> workspaceFactory)
{
    /// <summary>
    /// The active client sink, set when a server registers and cleared on deregistration.
    /// </summary>
    internal ILspClientSink? ActiveClientSink { get; private set; }

    /// <summary>
    /// Work done progress manager from the active server.
    /// Used by workspace-scoped components to report progress to the client.
    /// </summary>
    internal WorkDoneProgressManager? ActiveProgressManager { get; private set; }

    /// <summary>
    /// File change handler from the active server, used by <see cref="DelegatingFileChangeWatcher"/>
    /// to subscribe to LSP-based file change notifications.
    /// This is a temporary backdoor until file watching is properly decoupled from the server.
    /// </summary>
    internal LspDidChangeWatchedFilesHandler? ActiveFileChangeHandler { get; private set; }

    /// <summary>
    /// Initialize manager from the active server, providing access to client capabilities.
    /// Used by the file watcher to determine if LSP file watching is supported.
    /// Capabilities are lazily available — only valid after the LSP initialize request completes.
    /// This is a temporary backdoor until file watching is properly decoupled from the server.
    /// </summary>
    internal IInitializeManager? InitializeManager { get; private set; }

    /// <summary>
    /// Registers an LSP server and triggers workspace creation if needed.
    /// For the single-server model, only one server may be registered at a time.
    /// </summary>
    internal void RegisterServer(
        ILspClientSink sink,
        WorkDoneProgressManager progressManager,
        LspDidChangeWatchedFilesHandler fileChangeHandler,
        IInitializeManager initializeManager)
    {
        // Trigger workspace creation by forcing resolution of the lazy factory.
        // This has the side effect of creating the host and misc workspaces and
        // registering them via LspWorkspaceRegistrationEventListener.
        _ = workspaceFactory.Value;

        ActiveClientSink = sink;
        ActiveProgressManager = progressManager;
        ActiveFileChangeHandler = fileChangeHandler;
        InitializeManager = initializeManager;
    }

    /// <summary>
    /// Deregisters the active server and clears associated state.
    /// </summary>
    internal void DeregisterServer()
    {
        ActiveClientSink = null;
        ActiveProgressManager = null;
        ActiveFileChangeHandler = null;
        InitializeManager = null;
    }
}
