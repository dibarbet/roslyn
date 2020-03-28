// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Decompiler.IL;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor.UnitTests.Workspaces;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServices.Implementation.LanguageService;
using Moq;
using Nerdbank.Streams;
using Newtonsoft.Json.Linq;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using StreamJsonRpc;
using Xunit;
using LSP = Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Roslyn.VisualStudio.Next.UnitTests.Services
{
    [UseExportProvider]
    public class LspDiagnosticsTests : AbstractLanguageServerProtocolTests
    {
        [Fact]
        public async Task AddDiagnosticTestAsync()
        {
            using var workspace = CreateTestWorkspace("", out _);
            var document = workspace.CurrentSolution.Projects.First().Documents.First();

            var diagnosticsMock = new Mock<IDiagnosticService>();
            MockDocumentDiagnostics(diagnosticsMock, document.Id, await CreateMockDiagnosticDataAsync(document, "id").ConfigureAwait(false));

            var (clientStream, serverStream) = FullDuplexStream.CreatePair();
            var languageServer = CreateLanguageServer(serverStream, serverStream, workspace, diagnosticsMock.Object);

            var callback = new Callback(expectedNumberOfCallbacks: 1);
            using (var jsonRpc = JsonRpc.Attach(clientStream, callback))
            {
                await languageServer.PublishDiagnosticsAsync(document).ConfigureAwait(false);
                await callback.CallbackCompletedTask.Task.ConfigureAwait(false);

                var results = callback.Results;
                Assert.Equal(new Uri(document.FilePath, UriKind.Absolute), results.Single().Key);
                Assert.Equal("id", results.Single().Value.Single().Code);
            }
        }

        [Fact]
        public async Task AddDiagnosticWithMappedFilesTestAsync()
        {
            using var workspace = CreateTestWorkspace("", out _);
            var document = workspace.CurrentSolution.Projects.First().Documents.First();

            var diagnosticsMock = new Mock<IDiagnosticService>();
            MockDocumentDiagnostics(diagnosticsMock, document.Id, await CreateMockDiagnosticDatasWithMappedLocationAsync(document, "mapped1", "mapped2").ConfigureAwait(false));

            var (clientStream, serverStream) = FullDuplexStream.CreatePair();
            var languageServer = CreateLanguageServer(serverStream, serverStream, workspace, diagnosticsMock.Object);

            var callback = new Callback(expectedNumberOfCallbacks: 2);
            using (var jsonRpc = JsonRpc.Attach(clientStream, callback))
            {
                await languageServer.PublishDiagnosticsAsync(document).ConfigureAwait(false);
                await callback.CallbackCompletedTask.Task.ConfigureAwait(false);

                var results = callback.Results;
                Assert.Equal(2, results.Count);
                Assert.Equal("mapped1", results[new Uri(document.FilePath + "mapped1", UriKind.Absolute)].Single().Code);
                Assert.Equal("mapped2", results[new Uri(document.FilePath + "mapped2", UriKind.Absolute)].Single().Code);
            }
        }

        [Fact]
        public async Task AddDiagnosticWithMappedFileToManyDocumentsTestAsync()
        {
            using var workspace = CreateTestWorkspace(new string[] { "", "" }, out _);
            var documents = workspace.CurrentSolution.Projects.First().Documents.ToImmutableArray();

            var diagnosticsMock = new Mock<IDiagnosticService>();
            MockDocumentDiagnostics(diagnosticsMock, documents[0].Id, await CreateMockDiagnosticDatasWithMappedLocationAsync(documents[0], "mapped1", "mapped2").ConfigureAwait(false));
            MockDocumentDiagnostics(diagnosticsMock, documents[1].Id, await CreateMockDiagnosticDatasWithMappedLocationAsync(documents[1], "mapped2", "mapped3").ConfigureAwait(false));

            var (clientStream, serverStream) = FullDuplexStream.CreatePair();
            var languageServer = CreateLanguageServer(serverStream, serverStream, workspace, diagnosticsMock.Object);

            var callback = new Callback(expectedNumberOfCallbacks: 2);
            using (var jsonRpc = JsonRpc.Attach(clientStream, callback))
            {
                await languageServer.PublishDiagnosticsAsync(documents[0]).ConfigureAwait(false);
                await languageServer.PublishDiagnosticsAsync(documents[1]).ConfigureAwait(false);
                await callback.CallbackCompletedTask.Task.ConfigureAwait(false);

                var results = callback.Results;
                Assert.Equal(3, results.Count);
                Assert.Equal("mapped1", results[new Uri(documents[0].FilePath + "mapped1", UriKind.Absolute)].Single().Code);
                Assert.Equal("mapped2", results[new Uri(documents[0].FilePath + "mapped2", UriKind.Absolute)][0].Code);
                Assert.Equal("mapped2", results[new Uri(documents[1].FilePath + "mapped2", UriKind.Absolute)][1].Code);
                Assert.Equal("mapped3", results[new Uri(documents[1].FilePath + "mapped2", UriKind.Absolute)][1].Code);
            }
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

        private InProcLanguageServer CreateLanguageServer(Stream inputStream, Stream outputStream, Workspace workspace, IDiagnosticService mockDiagnosticService)
        {
            var protocol = ((TestWorkspace)workspace).ExportProvider.GetExportedValue<LanguageServerProtocol>();

            var languageServer = new InProcLanguageServer(inputStream, outputStream, protocol, workspace, mockDiagnosticService, clientName: "RazorCSharp");
            return languageServer;
        }

        private void MockDocumentDiagnostics(Mock<IDiagnosticService> diagnosticServiceMock, DocumentId documentId, IEnumerable<DiagnosticData> diagnostics)
        {
            diagnosticServiceMock.Setup(d => d.GetDiagnostics(It.IsAny<Workspace>(), It.IsAny<ProjectId>(), documentId,
                    It.IsAny<object>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Returns(diagnostics);
        }

        private async Task<IEnumerable<DiagnosticData>> CreateMockDiagnosticDataAsync(Document document, string id)
        {
            var descriptor = new DiagnosticDescriptor(id, "", "", "", DiagnosticSeverity.Error, true);
            var location = Location.Create(await document.GetSyntaxTreeAsync().ConfigureAwait(false), new TextSpan());
            return new DiagnosticData[] { DiagnosticData.Create(Diagnostic.Create(descriptor, location), document) };
        }

        private async Task<IEnumerable<DiagnosticData>> CreateMockDiagnosticDatasWithMappedLocationAsync(Document document, string firstId, string secondId)
        {
            var tree = await document.GetSyntaxTreeAsync().ConfigureAwait(false);
            return new DiagnosticData[]
            {
                CreateMockDiagnosticDataWithMappedLocation(document, tree, firstId, document.FilePath + firstId),
                CreateMockDiagnosticDataWithMappedLocation(document, tree, secondId, document.FilePath + secondId),
            };

            static DiagnosticData CreateMockDiagnosticDataWithMappedLocation(Document document, SyntaxTree tree, string id, string mappedFilePath)
            {
                var descriptor = new DiagnosticDescriptor(id, "", "", "", DiagnosticSeverity.Error, true);
                var location = Location.Create(tree, new TextSpan());

                var diagnostic = Diagnostic.Create(descriptor, location);
                return new DiagnosticData(diagnostic.Id,
                    diagnostic.Descriptor.Category,
                    null,
                    null,
                    diagnostic.Severity,
                    diagnostic.DefaultSeverity,
                    diagnostic.Descriptor.IsEnabledByDefault,
                    diagnostic.WarningLevel,
                    diagnostic.Descriptor.CustomTags.AsImmutableOrEmpty(),
                    diagnostic.Properties,
                    document.Project.Id,
                    GetDataLocation(document, mappedFilePath),
                    null,
                    document.Project.Language,
                    diagnostic.Descriptor.Title.ToString(),
                    diagnostic.Descriptor.Description.ToString(),
                    null,
                    diagnostic.IsSuppressed);
            }

            static DiagnosticDataLocation GetDataLocation(Document document, string mappedFilePath)
                => new DiagnosticDataLocation(document.Id, originalFilePath: document.FilePath, mappedFilePath: mappedFilePath);
        }

        private class Callback
        {
            /// <summary>
            /// Task that can be awaited for the callback to complete.
            /// </summary>
            public TaskCompletionSource<object> CallbackCompletedTask = new TaskCompletionSource<object>();

            public Dictionary<Uri, List<LSP.Diagnostic>> Results = new Dictionary<Uri, List<LSP.Diagnostic>>();

            private int _expectedNumberOfCallbacks;
            private int _currentNumberOfCallbacks;

            private object _lock = new object();

            public Callback(int expectedNumberOfCallbacks)
            {
                _expectedNumberOfCallbacks = expectedNumberOfCallbacks;
                _currentNumberOfCallbacks = 0;
            }

            [JsonRpcMethod(LSP.Methods.TextDocumentPublishDiagnosticsName)]
            public Task OnDiagnosticsPublished(JToken input)
            {
                lock(_lock)
                {
                    Contract.ThrowIfTrue(CallbackCompletedTask.Task.IsCompleted, "received too many callbacks");
                    _currentNumberOfCallbacks++;

                    var diagnosticParams = input.ToObject<LSP.PublishDiagnosticParams>();
                    Results[diagnosticParams.Uri] = diagnosticParams.Diagnostics.ToList();

                    if (_currentNumberOfCallbacks == _expectedNumberOfCallbacks)
                    {
                        CallbackCompletedTask.SetResult(new object());
                    }

                    return Task.CompletedTask;
                }
            }
        }
    }
}
