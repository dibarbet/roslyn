// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.Remote;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Nerdbank.Streams;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.LanguageService
{
    [ContentType("C#_LSP")]
    [ContentType("VB_LSP")]
    [Export(typeof(ILanguageClient))]
    internal class LiveShareLanguageServerClient : ILanguageClient
    {
        private class InProcLanguageServer
        {
            private readonly JsonRpc _jsonRpc;
            private readonly LanguageServerProtocol _protocol;
            private readonly Workspace _workspace;

            private VSClientCapabilities? _clientCapabilities;

            public InProcLanguageServer(Stream inputStream, Stream outputStream, LanguageServerProtocol protocol, Workspace workspace)
            {
                _protocol = protocol;
                _workspace = workspace;
                _jsonRpc = new JsonRpc(outputStream, inputStream, this);

                _jsonRpc.StartListening();
            }

            /// <summary>
            /// Handle the LSP initialize request by storing the client capabilities
            /// and responding with the server capabilities.
            /// The specification assures that the initialize request is sent only once.
            /// </summary>
            [JsonRpcMethod(Methods.InitializeName)]
            public InitializeResult Initialize(JToken input)
            {
                // InitializeParams only references ClientCapabilities, but the VS LSP client
                // sends additional VS specific capabilities, so directly deserialize them into the VSClientCapabilities
                // to avoid losing them.
                _clientCapabilities = input["capabilities"].ToObject<VSClientCapabilities>();

                return new InitializeResult
                {
                    Capabilities = new VSServerCapabilities
                    {
                        DefinitionProvider = true,
                        ReferencesProvider = true,
                        CompletionProvider = new CompletionOptions { ResolveProvider = true, TriggerCharacters = new[] { "." } },
                    }
                };
            }

            [JsonRpcMethod(Methods.InitializedName)]
            public Task Initialized() => Task.CompletedTask;

            [JsonRpcMethod(Methods.ShutdownName)]
            public object Shutdown(CancellationToken cancellationToken) => null;

            [JsonRpcMethod(Methods.ExitName)]
            public void Exit()
            {
            }

            [JsonRpcMethod(Methods.TextDocumentDefinitionName)]
            public async Task<object> GetTextDocumentDefinitionAsync(JToken input, CancellationToken cancellationToken)
            {
                var textDocumentPositionParams = input.ToObject<TextDocumentPositionParams>();
                return await _protocol.GoToDefinitionAsync(_workspace.CurrentSolution, textDocumentPositionParams, _clientCapabilities, cancellationToken).ConfigureAwait(false);
            }

            [JsonRpcMethod(Methods.TextDocumentCompletionName)]
            public async Task<object> GetTextDocumentCompletionAsync(JToken input, CancellationToken cancellationToken)
            {
                var completionParams = input.ToObject<CompletionParams>();
                return await _protocol.GetCompletionsAsync(_workspace.CurrentSolution, completionParams, _clientCapabilities, cancellationToken).ConfigureAwait(false);
            }

            [JsonRpcMethod(Methods.TextDocumentCompletionResolveName)]
            public async Task<object> ResolveCompletionItemAsync(JToken input, CancellationToken cancellationToken)
            {
                var completionItem = input.ToObject<CompletionItem>();
                return await _protocol.ResolveCompletionItemAsync(_workspace.CurrentSolution, completionItem, _clientCapabilities, cancellationToken).ConfigureAwait(false);
            }

            /*[JsonRpcMethod(Methods.TextDocumentReferencesName)]
            public async Task<object> GetTextDocumentReferencesAsync(JToken input, CancellationToken cancellationToken)
            {
                return;
            }*/
        }

        private readonly LanguageServerProtocol _languageServerProtocol;
        private readonly Workspace _workspace;

        /// <summary>
        /// Gets the name of the language client (displayed to the user).
        /// </summary>
        public string Name => "LiveShare C#/VB Language Server Client";

        /// <summary>
        /// Gets the configuration section names for the language client. This may be null if the language client
        /// does not provide settings.
        /// </summary>
        public virtual IEnumerable<string>? ConfigurationSections { get; } = null;

        /// <summary>
        /// Gets the initialization options object the client wants to send when 'initialize' message is sent.
        /// This may be null if the client does not need custom initialization options.
        /// </summary>
        public virtual object? InitializationOptions { get; } = null;

        /// <summary>
        /// Gets the list of file names to watch for changes.  Changes will be sent to the server via 'workspace/didChangeWatchedFiles'
        /// message.  The files to watch must be under the current active workspace.  The file names can be specified as a relative
        /// paths to the exact file, or as glob patterns following the standard in .gitignore see https://www.kernel.org/pub/software/scm/git/docs/gitignore.html files.
        /// </summary>
        public virtual IEnumerable<string>? FilesToWatch { get; } = null;

#pragma warning disable CS0067 // event never used - implementing interface ILanguageClient
        public event AsyncEventHandler<EventArgs>? StartAsync;
        public event AsyncEventHandler<EventArgs>? StopAsync;
#pragma warning restore CS0067 // event never used

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public LiveShareLanguageServerClient(LanguageServerProtocol languageServerProtocol, VisualStudioWorkspace workspace)
        {
            _languageServerProtocol = languageServerProtocol;
            _workspace = workspace;
        }

        public Task<Connection> ActivateAsync(CancellationToken token)
        {
            var (clientStream, serverStream) = FullDuplexStream.CreatePair();
            var server = new InProcLanguageServer(serverStream, serverStream, _languageServerProtocol, _workspace);
            return Task.FromResult(new Connection(clientStream, clientStream));
        }

        /// <summary>
        /// Signals that the extension has been loaded.  The server can be started immediately, or wait for user action to start.  
        /// To start the server, invoke the <see cref="StartAsync"/> event;
        /// </summary>
        public async Task OnLoadedAsync()
        {
            await (StartAsync?.InvokeAsync(this, EventArgs.Empty)).ConfigureAwait(false);
        }

        /// <summary>
        /// Signals the extension that the language server has been successfully initialized.
        /// </summary>
        public Task OnServerInitializedAsync()
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Signals the extension that the language server failed to initialize.
        /// </summary>
        public Task OnServerInitializeFailedAsync(Exception e)
        {
            return Task.CompletedTask;
        }
    }
}
