// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis.Internal.Log;
using Roslyn.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.Logging;

/// <summary>
/// Implements Roslyn's <see cref="ILogger"/> backed by OpenTelemetry traces via <see cref="ActivitySource"/>.
/// <para>
/// <see cref="ILogger.Log"/> creates a zero-duration <see cref="Activity"/> (point-in-time event).
/// <see cref="ILogger.LogBlockStart"/>/<see cref="ILogger.LogBlockEnd"/> create an <see cref="Activity"/> with duration.
/// </para>
/// <para>
/// All activities use <see cref="ActivitySource"/> named <c>"Roslyn.Logger"</c> so that exporters
/// can filter on source name to differentiate from LSP request spans (<c>"Roslyn.LanguageServer"</c>).
/// </para>
/// </summary>
internal sealed class OTelRoslynLogger : ILogger
{
    internal const string SourceName = OpenTelemetrySourceNames.RoslynLogger;
    internal static readonly ActivitySource ActivitySource = new(SourceName);

    /// <summary>
    /// Tracks pending LogBlock activities keyed by blockId.
    /// </summary>
    private readonly ConcurrentDictionary<int, Activity> _pendingActivities = new(concurrencyLevel: 2, capacity: 10);

    public bool IsEnabled(FunctionId functionId) => ActivitySource.HasListeners();

    public void Log(FunctionId functionId, LogMessage logMessage)
    {
        // Create a zero-duration activity representing a single telemetry event.
        using var activity = ActivitySource.StartActivity(
            TelemetryNaming.GetEventName(functionId),
            ActivityKind.Internal);

        if (activity is null)
            return;

        SetCommonTags(activity, functionId, logMessage);
    }

    public void LogBlockStart(FunctionId functionId, LogMessage logMessage, int uniquePairId, CancellationToken cancellationToken)
    {
        var activity = ActivitySource.StartActivity(
            TelemetryNaming.GetEventName(functionId),
            ActivityKind.Internal);

        if (activity is null)
            return;

        activity.SetTag("roslyn.block_id", uniquePairId);
        SetCommonTags(activity, functionId, logMessage);

        _pendingActivities[uniquePairId] = activity;
    }

    public void LogBlockEnd(FunctionId functionId, LogMessage logMessage, int uniquePairId, int delta, CancellationToken cancellationToken)
    {
        if (!_pendingActivities.TryRemove(uniquePairId, out var activity))
            return;

        activity.SetTag("roslyn.delta", delta);
        activity.SetTag("roslyn.cancelled", cancellationToken.IsCancellationRequested);

        // Add any additional properties from the end message
        if (logMessage is KeyValueLogMessage kvLogMessage)
        {
            foreach (var (name, val) in kvLogMessage.Properties)
            {
                activity.SetTag("roslyn.prop." + name.ToLowerInvariant(), val?.ToString());
            }
        }

        activity.Dispose();
    }

    private static void SetCommonTags(Activity activity, FunctionId functionId, LogMessage logMessage)
    {
        activity.SetTag("roslyn.function_id", (int)functionId);
        activity.SetTag("roslyn.function_name", functionId.ToString());
        activity.SetTag("roslyn.log_type", (int)TelemetryNaming.GetKind(logMessage));

        if (logMessage is KeyValueLogMessage kvLogMessage)
        {
            foreach (var (name, val) in kvLogMessage.Properties)
            {
                activity.SetTag("roslyn.prop." + name.ToLowerInvariant(), val?.ToString());
            }
        }
        else
        {
            var message = logMessage.GetMessage();
            if (!string.IsNullOrEmpty(message))
            {
                activity.SetTag("roslyn.message", message);
            }
        }
    }
}
