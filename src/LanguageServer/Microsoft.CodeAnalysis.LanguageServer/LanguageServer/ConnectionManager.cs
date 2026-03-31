// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.LanguageServer.HostWorkspace.FileWatching;
using Microsoft.CodeAnalysis.LanguageServer.Logging;
using Microsoft.CommonLanguageServerProtocol.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Composition;
using StreamJsonRpc;

namespace Microsoft.CodeAnalysis.LanguageServer.LanguageServer;

/// <summary>
/// Manages the lifecycle of a single LSP server connection.
/// Creates the transport, server instance, and registers with the <see cref="SharedWorkspaceManager"/>.
/// In daemon mode, multiple <see cref="ConnectionManager"/> instances would exist (one per client),
/// but for now this handles the single-server case.
/// </summary>
#pragma warning disable CA1001 // The JsonRpc instance is disposed of by the AbstractLanguageServer during shutdown
internal sealed class ConnectionManager
#pragma warning restore CA1001
{
    private readonly ILogger _logger;
    private readonly AbstractLanguageServer<RequestContext> _roslynLanguageServer;
    private readonly JsonRpc _jsonRpc;
    private readonly SharedWorkspaceManager _sharedWorkspaceManager;

    public ConnectionManager(
        Stream inputStream,
        Stream outputStream,
        ExportProvider exportProvider,
        ILoggerFactory loggerFactory,
        AbstractTypeRefResolver typeRefResolver,
        SharedWorkspaceManager sharedWorkspaceManager)
    {
        _sharedWorkspaceManager = sharedWorkspaceManager;

        var messageFormatter = RoslynLanguageServer.CreateJsonMessageFormatter();
        var handler = new HeaderDelimitedMessageHandler(outputStream, inputStream, messageFormatter);

        _jsonRpc = new JsonRpc(handler)
        {
            ExceptionStrategy = ExceptionProcessing.CommonErrorData,
        };

        var roslynLspFactory = exportProvider.GetExportedValue<ILanguageServerFactory>();

        _logger = loggerFactory.CreateLogger("LSP");
        var lspLogger = new LspServiceLogger(_logger);

        var hostServices = exportProvider.GetExportedValue<HostServicesProvider>().HostServices;
        _roslynLanguageServer = roslynLspFactory.Create(
            _jsonRpc,
            messageFormatter.JsonSerializerOptions,
            WellKnownLspServerKinds.CSharpVisualBasicLspServer,
            lspLogger,
            hostServices,
            typeRefResolver);
    }

    public void Start()
    {
        _jsonRpc.StartListening();

        // Register the server's client sink with the SharedWorkspaceManager.
        // This creates the workspace (if not yet created) and makes the sink
        // available to workspace-scoped components.
        var lspServices = _roslynLanguageServer.GetLspServices();
        var clientManager = lspServices.GetRequiredService<IClientLanguageServerManager>();
        var progressManager = lspServices.GetRequiredService<WorkDoneProgressManager>();
        var fileChangeHandler = lspServices.GetRequiredService<LspDidChangeWatchedFilesHandler>();
        var initializeManager = lspServices.GetRequiredService<IInitializeManager>();
        var sink = new LspClientSink(clientManager);
        _sharedWorkspaceManager.RegisterServer(sink, progressManager, fileChangeHandler, initializeManager);
    }

    public async Task WaitForExitAsync()
    {
        try
        {
            await _jsonRpc.Completion;
        }
        catch (Exception)
        {
            // The JsonRpc connection threw an exception. This usually means the client
            // disconnected unexpectedly. We handle it and let the process exit.
        }

        await _roslynLanguageServer.WaitForExitAsync();

        _sharedWorkspaceManager.DeregisterServer();
    }

    public T GetRequiredLspService<T>() where T : ILspService
    {
        return _roslynLanguageServer.GetLspServices().GetRequiredService<T>();
    }
}
