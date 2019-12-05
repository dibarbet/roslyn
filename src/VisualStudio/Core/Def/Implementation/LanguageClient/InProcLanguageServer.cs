// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.LanguageService
{
    internal class InProcLanguageServer
    {
        private readonly IDiagnosticService _diagnosticService;
        private readonly IThreadingContext _threadingContext;

        private readonly JsonRpc _jsonRpc;
        private readonly LanguageServerProtocol _protocol;
        private readonly Workspace _workspace;

        private VSClientCapabilities? _clientCapabilities;

        public InProcLanguageServer(Stream inputStream, Stream outputStream, LanguageServerProtocol protocol, Workspace workspace, IDiagnosticService diagnosticService, IThreadingContext threadingContext)
        {
            this._protocol = protocol;
            this._workspace = workspace;

            this._jsonRpc = new JsonRpc(outputStream, inputStream, this);
            this._jsonRpc.StartListening();

            this._diagnosticService = diagnosticService ?? throw new ArgumentNullException(nameof(diagnosticService));
            this._diagnosticService.DiagnosticsUpdated += DiagnosticService_DiagnosticsUpdated;

            this._threadingContext = threadingContext;
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
            this._clientCapabilities = input["capabilities"].ToObject<VSClientCapabilities>();

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
            return await this._protocol.GoToDefinitionAsync(_workspace.CurrentSolution, textDocumentPositionParams, _clientCapabilities, cancellationToken).ConfigureAwait(false);
        }

        [JsonRpcMethod(Methods.TextDocumentCompletionName)]
        public async Task<object> GetTextDocumentCompletionAsync(JToken input, CancellationToken cancellationToken)
        {
            var completionParams = input.ToObject<CompletionParams>();
            return await this._protocol.GetCompletionsAsync(_workspace.CurrentSolution, completionParams, _clientCapabilities, cancellationToken).ConfigureAwait(false);
        }

        [JsonRpcMethod(Methods.TextDocumentCompletionResolveName)]
        public async Task<object> ResolveCompletionItemAsync(JToken input, CancellationToken cancellationToken)
        {
            var completionItem = input.ToObject<CompletionItem>();
            return await this._protocol.ResolveCompletionItemAsync(_workspace.CurrentSolution, completionItem, _clientCapabilities, cancellationToken).ConfigureAwait(false);
        }

        [JsonRpcMethod(Methods.TextDocumentReferencesName)]
        public async Task<object> GetTextDocumentReferencesAsync(JToken input, CancellationToken cancellationToken)
        {
            var referenceParams = input.ToObject<ReferenceParams>();

            // References are retrieved from third parties which requires the main thread (e.g. xaml).
            await _threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return await this._protocol.FindReferencesAsync(_workspace.CurrentSolution, referenceParams, _clientCapabilities, cancellationToken).ConfigureAwait(false);
        }

#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void DiagnosticService_DiagnosticsUpdated(object sender, DiagnosticsUpdatedArgs e)
#pragma warning restore VSTHRD100 // Avoid async void methods
        {
            // Since this is an async void method, exceptions here will crash the host VS. We catch exceptions here to make sure that we don't crash the host since
            // the worst outcome here is that guests may not see all diagnostics.
            try
            {
                // LSP doesnt support diagnostics without a document. So if we get project level diagnostics without a document, ignore them.
                if (e.DocumentId != null && e.Solution != null)
                {
                    var document = e.Solution.GetDocument(e.DocumentId);
                    if (document == null || document.FilePath == null)
                    {
                        return;
                    }

                    // Only publish document diagnostics for the languages this provider supports.
                    if (document.Project.Language != LanguageNames.CSharp && document.Project.Language != LanguageNames.VisualBasic)
                    {
                        return;
                    }

                    // LSP does not currently support publishing diagnostics incrememntally, so we re-publish all diagnostics.
                    var diagnostics = await GetDiagnosticsAsync(e.Solution, document, CancellationToken.None).ConfigureAwait(false);
                    var publishDiagnosticsParams = new PublishDiagnosticParams { Diagnostics = diagnostics, Uri = document.GetURI() };
                    await this._jsonRpc.NotifyWithParameterObjectAsync(Methods.TextDocumentPublishDiagnosticsName, publishDiagnosticsParams).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (FatalError.ReportWithoutCrash(ex))
            {
            }
        }

        private async Task<LanguageServer.Protocol.Diagnostic[]> GetDiagnosticsAsync(Solution solution, Document document, CancellationToken cancellationToken)
        {
            var diagnostics = this._diagnosticService.GetDiagnostics(solution.Workspace, document.Project.Id, document.Id, null, false, cancellationToken);
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);

            return diagnostics.Select(diagnostic => new LanguageServer.Protocol.Diagnostic
            {
                Code = diagnostic.Id,
                Message = diagnostic.Message,
                Severity = ProtocolConversions.DiagnosticSeverityToLspDiagnositcSeverity(diagnostic.Severity),
                Range = ProtocolConversions.TextSpanToRange(diagnostic.GetExistingOrCalculatedTextSpan(text), text),
                // Only the unnecessary diagnostic tag is currently supported via LSP.
                Tags = diagnostic.CustomTags.Contains("Unnecessary") ? new DiagnosticTag[] { DiagnosticTag.Unnecessary } : Array.Empty<DiagnosticTag>()
            }).ToArray();
        }
    }
}
