// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.LanguageServer.Logging;
using Roslyn.LanguageServer.Protocol;
using Xunit.Abstractions;
using LogLevel = Microsoft.CodeAnalysis.Internal.Log.LogLevel;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests;

public sealed class OTelIntegrationTests(ITestOutputHelper testOutputHelper)
    : AbstractLanguageServerHostTests(testOutputHelper), IDisposable
{
    private readonly ConcurrentBag<Activity> _completedActivities = [];
    private ActivityListener? _activityListener;
    private ILogger? _previousLogger;

    private void SetupActivityListener()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is OTelRoslynLogger.SourceName or OpenTelemetrySourceNames.LanguageServer,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _completedActivities.Add(activity),
        };
        ActivitySource.AddActivityListener(_activityListener);
    }

    private void SetupOTelLogger()
    {
        _previousLogger = Logger.SetLogger(new OTelRoslynLogger());
    }

    public new void Dispose()
    {
        Logger.SetLogger(_previousLogger);
        _activityListener?.Dispose();
        base.Dispose();
    }

    [Fact]
    public async Task LspRequest_ProducesLanguageServerActivity()
    {
        SetupActivityListener();

        await using var server = await CreateLanguageServerAsync();

        var document = new VersionedTextDocumentIdentifier { DocumentUri = ProtocolConversions.CreateAbsoluteDocumentUri(@"C:\test.cs") };
        await server.ExecuteRequestAsync<DidOpenTextDocumentParams, object>(Methods.TextDocumentDidOpenName, new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                DocumentUri = document.DocumentUri,
                Text = "class C { }"
            }
        }, CancellationToken.None);

        // Verify activities were created from the Roslyn.LanguageServer source for the request.
        var lspActivities = _completedActivities.Where(a => a.Source.Name == OpenTelemetrySourceNames.LanguageServer).ToList();
        Assert.NotEmpty(lspActivities);

        var requestActivity = lspActivities.Single(a => a.OperationName == "lsp/textDocument/didOpen");
        Assert.Equal(OpenTelemetrySourceNames.LanguageServer, requestActivity.Source.Name);
        Assert.NotNull(requestActivity.GetTagItem("lsp.method"));
    }

    [Fact]
    public async Task LspRequest_ProducesParentAndChildExecuteActivities()
    {
        SetupActivityListener();

        await using var server = await CreateLanguageServerAsync();

        var document = new VersionedTextDocumentIdentifier { DocumentUri = ProtocolConversions.CreateAbsoluteDocumentUri(@"C:\test.cs") };
        await server.ExecuteRequestAsync<DidOpenTextDocumentParams, object>(Methods.TextDocumentDidOpenName, new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                DocumentUri = document.DocumentUri,
                Text = "class C { }"
            }
        }, CancellationToken.None);

        var lspActivities = _completedActivities.Where(a => a.Source.Name == OpenTelemetrySourceNames.LanguageServer).ToList();

        // Should have a parent activity for the full request and a child for execution.
        var parentActivity = lspActivities.Single(a => a.OperationName == "lsp/textDocument/didOpen");
        var executeActivity = lspActivities.Single(a => a.OperationName == "lsp/textDocument/didOpen/execute");

        // Child should reference the parent.
        Assert.Equal(parentActivity.TraceId, executeActivity.TraceId);
        Assert.Equal(parentActivity.SpanId, executeActivity.ParentSpanId);
    }

    [Fact]
    public void LoggerLog_ProducesZeroDurationActivity()
    {
        SetupActivityListener();
        SetupOTelLogger();

        Logger.Log(FunctionId.Formatting_Format, LogMessage.Create("test message", LogLevel.Information));

        var activity = _completedActivities.Single(a => a.Source.Name == OTelRoslynLogger.SourceName);
        Assert.Equal((int)FunctionId.Formatting_Format, activity.GetTagItem("roslyn.function_id"));
        Assert.Equal("test message", activity.GetTagItem("roslyn.message"));
    }

    [Fact]
    public void LoggerLogBlock_ProducesActivityWithDuration()
    {
        SetupActivityListener();
        SetupOTelLogger();

        using (Logger.LogBlock(FunctionId.Formatting_Format, LogMessage.Create("block test", LogLevel.Information), CancellationToken.None))
        {
            // Simulate some work
            Thread.Sleep(10);
        }

        var activity = _completedActivities.Single(a => a.Source.Name == OTelRoslynLogger.SourceName);
        Assert.Equal((int)FunctionId.Formatting_Format, activity.GetTagItem("roslyn.function_id"));
        Assert.True(activity.Duration > TimeSpan.Zero, "LogBlock activity should have non-zero duration");
    }

    [Fact]
    public void LoggerLog_DoesNotProduceActivity_WhenNoListeners()
    {
        // Don't set up listener — should not produce activities.
        SetupOTelLogger();

        Logger.Log(FunctionId.Formatting_Format, LogMessage.Create("no listener", LogLevel.Information));

        Assert.Empty(_completedActivities);
    }

    [Fact]
    public async Task LspRequest_ActivityHasCorrectTags()
    {
        SetupActivityListener();

        await using var server = await CreateLanguageServerAsync();

        var document = new VersionedTextDocumentIdentifier { DocumentUri = ProtocolConversions.CreateAbsoluteDocumentUri(@"C:\test.cs") };
        await server.ExecuteRequestAsync<DidOpenTextDocumentParams, object>(Methods.TextDocumentDidOpenName, new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                DocumentUri = document.DocumentUri,
                Text = "class C { }"
            }
        }, CancellationToken.None);

        var requestActivity = _completedActivities.Single(
            a => a.Source.Name == OpenTelemetrySourceNames.LanguageServer && a.OperationName == "lsp/textDocument/didOpen");

        // Verify expected tags from RequestTelemetryScope
        Assert.Equal("textDocument/didOpen", requestActivity.GetTagItem("lsp.method"));
        Assert.NotNull(requestActivity.GetTagItem("lsp.result"));
        Assert.NotNull(requestActivity.GetTagItem("lsp.duration_ms"));
    }

    [Fact]
    public void RoslynLoggerActivities_UseDifferentSourceFromLspRequests()
    {
        SetupActivityListener();
        SetupOTelLogger();

        Logger.Log(FunctionId.Formatting_Format, LogMessage.Create("test", LogLevel.Information));

        var activity = _completedActivities.Single();
        Assert.Equal(OTelRoslynLogger.SourceName, activity.Source.Name);
        Assert.NotEqual(OpenTelemetrySourceNames.LanguageServer, activity.Source.Name);
    }

    [Fact]
    public void LoggerLog_KeyValueLogMessage_SetsProperties()
    {
        SetupActivityListener();
        SetupOTelLogger();

        Logger.Log(FunctionId.Formatting_Format, KeyValueLogMessage.Create(m =>
        {
            m["ProjectName"] = "TestProject";
            m["Count"] = "42";
        }));

        var activity = _completedActivities.Single(a => a.Source.Name == OTelRoslynLogger.SourceName);
        Assert.Equal("TestProject", activity.GetTagItem("roslyn.prop.projectname"));
        Assert.Equal("42", activity.GetTagItem("roslyn.prop.count"));
    }
}
