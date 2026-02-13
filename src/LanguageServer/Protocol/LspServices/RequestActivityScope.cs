// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CommonLanguageServerProtocol.Framework;
using Roslyn.LanguageServer.Protocol;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.LanguageServer;

internal sealed class RequestActivityScope : AbstractRequestScope
{
    private static readonly ActivitySource s_activitySource = new(OpenTelemetryConstants.LanguageServer);

    private readonly RequestTelemetryLogger _telemetryLogger;

    /// <summary>
    /// Records an activity for the entire lifetime of the LSP request, including time in queue.
    /// </summary>
    private readonly Activity? _activity;

    /// <summary>
    /// Records an activity for just the execution phase of the LSP request as a child of the overall request activity.
    /// </summary>
    private Activity? _executeActivity;
    private RequestTelemetryLogger.Result _result = RequestTelemetryLogger.Result.Succeeded;
    private readonly SharedStopwatch _stopwatch = SharedStopwatch.StartNew();
    private TimeSpan _queuedDuration;

    public RequestActivityScope(string name, RequestTelemetryLogger telemetryLogger)
        : base(name)
    {
        _telemetryLogger = telemetryLogger;
        _activity = s_activitySource.StartActivity($"lsp/{name}", ActivityKind.Server);
        _activity?.SetTag("lsp.method", name);
    }

    public override void RecordExecutionStart()
    {
        _queuedDuration = _stopwatch.Elapsed;
        _activity?.SetTag("lsp.queue_duration_ms", _queuedDuration.TotalMilliseconds);

        // Start a child activity for the execution phase so traces show
        // both the total request lifetime and the handler execution separately.
        _executeActivity = s_activitySource.StartActivity($"lsp/{Name}/execute", ActivityKind.Internal, _activity?.Context ?? default);
    }

    public override void RecordCancellation()
    {
        _result = RequestTelemetryLogger.Result.Cancelled;
    }

    public override void RecordException(Exception exception)
    {
        // Report a NFW report for the request failure, as well as recording statistics on the failure.
        ReportNonFatalError(exception);

        _result = RequestTelemetryLogger.Result.Failed;
        _executeActivity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            { "exception.type", exception.GetType().FullName },
            { "exception.message", exception.Message },
        }));
    }

    public override void RecordWarning(string message)
    {
        _result = RequestTelemetryLogger.Result.Failed;
        _executeActivity?.AddEvent(new ActivityEvent("warning", tags: new ActivityTagsCollection
        {
            { "message", message },
        }));
    }

    public override void Dispose()
    {
        var requestDuration = _stopwatch.Elapsed;

        // Set final status on both activities based on the result.
        var status = _result switch
        {
            RequestTelemetryLogger.Result.Succeeded => ActivityStatusCode.Ok,
            RequestTelemetryLogger.Result.Cancelled => ActivityStatusCode.Ok,
            RequestTelemetryLogger.Result.Failed => ActivityStatusCode.Error,
            _ => ActivityStatusCode.Unset,
        };

        _executeActivity?.SetStatus(status, _result.ToString());
        _executeActivity?.Dispose();

        _activity?.SetStatus(status, _result.ToString());
        _activity?.SetTag("lsp.language", Language);
        _activity?.SetTag("lsp.result", _result.ToString());
        _activity?.Dispose();

        _telemetryLogger.UpdateTelemetryData(Name, Language, _queuedDuration, requestDuration, _result);
    }

    private static void ReportNonFatalError(Exception exception)
    {
        if (exception is StreamJsonRpc.LocalRpcException localRpcException && localRpcException.ErrorCode == LspErrorCodes.ContentModified)
        {
            // We throw content modified exceptions when asked to resolve code lens / inlay hints associated with a solution version we no longer have.
            // This generally happens when the project changes underneath us.  The client is eventually told to refresh,
            // but they can send us resolve requests for prior versions before they see the refresh.
            // There is no need to report these exceptions as NFW since they are expected to occur in normal workflows.
            return;
        }

        FatalError.ReportAndPropagateUnlessCanceled(exception, ErrorSeverity.Critical);
    }
}
