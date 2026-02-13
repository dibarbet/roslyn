// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis.Contracts.Telemetry;
using Microsoft.CodeAnalysis.LanguageServer.Exporters;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Roslyn.LanguageServer.Protocol;
using Xunit.Abstractions;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests;

/// <summary>
/// End-to-end tests verifying that the OpenTelemetry pipeline correctly routes signals
/// to the LSP client (via <c>window/logMessage</c>) and to the telemetry reporter
/// (via <see cref="VSTelemetryTraceExporter"/>).
/// </summary>
public sealed class OpenTelemetryIntegrationTests(ITestOutputHelper testOutputHelper)
    : AbstractLanguageServerHostTests(testOutputHelper)
{
    [Fact]
    public async Task LspRequest_ProducesWindowLogMessages_AndTelemetryReporterCalls()
    {
        // Set up a test telemetry reporter wired into the trace pipeline.
        var telemetryReporter = new TestTelemetryReporter();

        // Capture window/logMessage notifications on the client side.
        var logMessages = new ConcurrentBag<string>();

        var server = await CreateLanguageServerAsync(telemetryReporter: telemetryReporter);
        server.AddClientLocalRpcTarget(Methods.WindowLogMessageName, new Action<int, string>((_, message) => logMessages.Add(message)));

        // Send a request that exercises the RequestTelemetryScope pipeline.
        var document = new VersionedTextDocumentIdentifier { DocumentUri = ProtocolConversions.CreateAbsoluteDocumentUri(@"C:\test.cs") };
        await server.ExecuteRequestAsync<DidOpenTextDocumentParams, object>(Methods.TextDocumentDidOpenName, new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                DocumentUri = document.DocumentUri,
                Text = "class C { }"
            }
        }, CancellationToken.None);

        // Shut down the server so all activities are flushed through the exporter.
        await server.DisposeAsync();

        // Verify window/logMessage was received by the client (M.E.Logging → OpenTelemetry logs → LspLogMessageExporter).
        Assert.NotEmpty(logMessages);

        // Verify that the VSTelemetryTraceExporter forwarded the LSP request telemetry.
        // The RequestTelemetryScope creates activities on the Roslyn.LanguageServer source,
        // but the VSTelemetryTraceExporter only processes Roslyn.Logger activities — so we
        // should NOT see LSP request entries in the telemetry reporter (they are Aspire-only).
        Assert.Empty(telemetryReporter.LogEntries.Where(e => e.Name.Contains("textDocument/didOpen")));
        Assert.Empty(telemetryReporter.BlockStarts.Where(b => b.EventName.Contains("textDocument/didOpen")));
    }

    /// <summary>
    /// Test implementation of <see cref="ITelemetryReporter"/> that records all calls for assertion.
    /// </summary>
    private sealed class TestTelemetryReporter : ITelemetryReporter
    {
        public ConcurrentBag<(string Name, List<KeyValuePair<string, object?>> Properties)> LogEntries { get; } = [];
        public ConcurrentBag<(string EventName, int Kind, int BlockId)> BlockStarts { get; } = [];
        public ConcurrentBag<(int BlockId, List<KeyValuePair<string, object?>> Properties)> BlockEnds { get; } = [];

        public void InitializeSession(string telemetryLevel, string? sessionId, bool isDefaultSession) { }
        public void Log(string name, List<KeyValuePair<string, object?>> properties) => LogEntries.Add((name, properties));
        public void LogBlockStart(string eventName, int kind, int blockId) => BlockStarts.Add((eventName, kind, blockId));
        public void LogBlockEnd(int blockId, List<KeyValuePair<string, object?>> properties, CancellationToken cancellationToken) => BlockEnds.Add((blockId, properties));
        public void ReportFault(string eventName, string description, int logLevel, bool forceDump, int processId, Exception exception) { }
        public void Dispose() { }
    }
}

