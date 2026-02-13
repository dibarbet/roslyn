// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.Telemetry;
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
/// can filter on source name to differentiate from non-telemetry spans.
/// </para>
/// </summary>
internal sealed class OpenTelemetryRoslynLogger(bool logDelta) : AbstractTelemetryLogger<Activity, Activity>
{
    internal const string SourceName = OpenTelemetryConstants.RoslynLogger;
    internal static readonly ActivitySource ActivitySource = new(SourceName);

    protected override bool LogDelta => logDelta;

    public override bool IsEnabled(FunctionId functionId)
    {
        return ActivitySource.HasListeners();
    }

    protected override Activity BlockStart(string eventName, LogType type)
    {
        // Create a zero-duration activity representing a single telemetry event.
        using var activity = ActivitySource.StartActivity(eventName, ActivityKind.Internal);

        Contract.ThrowIfNull(activity, $"{nameof(BlockStart)} should only be called if IsEnabled is true and there is an active listener.");

        // TODO - respect in impl.
        activity.SetTag(OpenTelemetryConstants.LogTypeKey, (int)type);
        return activity;
    }

    protected override void BlockEnd(Activity scope, bool cancelled)
    {
        scope.SetTag(OpenTelemetryConstants.CancelledKey, cancelled);
        scope.Dispose();
    }

    protected override Activity GetEndEvent(Activity scope)
    {
        return scope;
    }

    protected override Activity CreateTelemetryEvent(string eventName)
    {
        // Create a zero-duration activity representing a single telemetry event.
        using var activity = ActivitySource.StartActivity(eventName, ActivityKind.Internal);

        Contract.ThrowIfNull(activity, $"{nameof(CreateTelemetryEvent)} should only be called if IsEnabled is true and there is an active listener.");
        return activity;
    }

    protected override void PostEvent(Activity telemetryEvent)
    {
        telemetryEvent.Dispose();
    }

    protected override void AddProperty(string propertyName, object? value, Activity telemetryEvent)
    {
        telemetryEvent.SetTag(propertyName, value);
    }

    protected override void AddProperties(string propertyName, IEnumerable<object?> items, Activity telemetryEvent)
    {
        // This is later turned into a complex property when telemetry is reported by the exporter.
        telemetryEvent.SetTag(propertyName, items);
    }

    protected override object CreatePiiProperty(PiiValue value)
    {
        // This is later handled when telemetry is reported by the exporter.
        return value;
    }
}
