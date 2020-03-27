// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Decompiler.IL;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor.UnitTests;
using Microsoft.CodeAnalysis.Editor.UnitTests.Workspaces;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.LanguageServer.Handler.Commands;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Composition;
using Microsoft.VisualStudio.LanguageServices.Implementation.LanguageService;
using Moq;
using Nerdbank.Streams;
using Roslyn.Test.Utilities;
using StreamJsonRpc;
using Xunit;
using Xunit.Sdk;
using LSP = Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Roslyn.VisualStudio.Next.UnitTests.Services
{
    [UseExportProvider]
    public class LspDiagnosticsTests
    {
        [Fact]
        public async Task AddDiagnosticTestAsync()
        {
            using var workspace = TestWorkspace.CreateCSharp("", exportProvider: GetExportProvider());

            var diagnosticsMock = new Mock<IDiagnosticService>();
            diagnosticsMock.Setup(d => d.GetDiagnostics(workspace, workspace.Projects.First().Id, workspace.Documents.First().Id, It.IsAny<object>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Returns(new DiagnosticData[] { await CreateMockDiagnosticDataAsync(workspace).ConfigureAwait(false) });

            var (languageServer, callback) = CreateLanguageServer(workspace, diagnosticsMock.Object);

            var results = await GetPublishDiagnosticResultsAsync(workspace, languageServer, callback);

            Assert.Equal(1, results.Count);
        }

        [Fact]
        public void AddDiagnosticWithMappedFilesTestAsync()
        {

        }

        [Fact]
        public void AddDiagnosticWithMappedFileToManyDocumentsTestAsync()
        {

        }

        [Fact]
        public void RemoveDiagnosticTestAsync()
        {

        }

        [Fact]
        public void RemoveDiagnosticForMappedFilesTestAsync()
        {

        }

        [Fact]
        public void RemoveDiagnosticForMappedFileToManyDocumentsTestAsync()
        {

        }

        [Fact]
        public void ClearAllDiagnosticsTestAsync()
        {

        }

        [Fact]
        public void ClearAllDiagnosticsForMappedFilesTestAsync()
        {

        }

        [Fact]
        public void ClearAllDiagnosticsForMappedFileToManyDocumentsTestAsync()
        {

        }

        private async Task<Dictionary<Uri, List<LSP.Diagnostic>>> GetPublishDiagnosticResultsAsync(TestWorkspace workspace,
            InProcLanguageServer languageServer, Callback callback)
        {
            await languageServer.PublishDiagnosticsAsync(workspace.CurrentSolution.GetDocument(workspace.Documents.First().Id));
            return callback.Results;
        }

        private (InProcLanguageServer, Callback) CreateLanguageServer(TestWorkspace workspace, IDiagnosticService mockDiagnosticService)
        {
            var protocol = workspace.ExportProvider.GetExportedValue<LanguageServerProtocol>();
            var (_, serverStream) = FullDuplexStream.CreatePair();
            var languageServer = new InProcLanguageServer(serverStream, serverStream, protocol, workspace, mockDiagnosticService, clientName: "RazorCSharp");
            var callback = new Callback();
            using var jsonRpc = JsonRpc.Attach(serverStream, callback);

            return (languageServer, callback);
        }

        private ExportProvider GetExportProvider()
        {
            var requestHelperTypes = DesktopTestHelpers.GetAllTypesImplementingGivenInterface(
                    typeof(IRequestHandler).Assembly, typeof(IRequestHandler));
            var executeCommandHandlerTypes = DesktopTestHelpers.GetAllTypesImplementingGivenInterface(
                    typeof(IExecuteWorkspaceCommandHandler).Assembly, typeof(IExecuteWorkspaceCommandHandler));
            var exportProviderFactory = ExportProviderCache.GetOrCreateExportProviderFactory(
                TestExportProvider.EntireAssemblyCatalogWithCSharpAndVisualBasic
                .WithPart(typeof(LanguageServerProtocol))
                .WithParts(requestHelperTypes)
                .WithParts(executeCommandHandlerTypes));
            return exportProviderFactory.CreateExportProvider();
        }

        private async Task<DiagnosticData> CreateMockDiagnosticDataAsync(TestWorkspace workspace)
        {
            var descriptor = new DiagnosticDescriptor("testId", "test", "", "test", DiagnosticSeverity.Error, true);
            var document = workspace.CurrentSolution.GetDocument(workspace.Documents.First().Id);
            var location = Location.Create(await document.GetSyntaxTreeAsync().ConfigureAwait(false), new TextSpan());
            return DiagnosticData.Create(Diagnostic.Create(descriptor, location), document);
        }

        private DiagnosticsUpdatedArgs GetDiagnosticsUpdatedArgs(TestWorkspace workspace)
        {
            return DiagnosticsUpdatedArgs.DiagnosticsCreated(new object(), workspace, workspace.CurrentSolution,
                workspace.Projects.First().Id, workspace.Documents.First().Id, ImmutableArray<DiagnosticData>.Empty);
        }

        private class Callback
        {
            public Dictionary<Uri, List<LSP.Diagnostic>> Results = new Dictionary<Uri, List<LSP.Diagnostic>>();

            [JsonRpcMethod(LSP.Methods.TextDocumentPublishDiagnosticsName)]
            public Task OnDiagnosticsPublished(LSP.PublishDiagnosticParams diagnosticParams)
            {
                Results[diagnosticParams.Uri] = diagnosticParams.Diagnostics.ToList();

                return Task.CompletedTask;
            }
        }
    }
}
