// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.VisualStudio.Telemetry;
using Microsoft.VisualStudio.Telemetry.Metrics;
using Microsoft.VisualStudio.Telemetry.Metrics.Events;

namespace Microsoft.CodeAnalysis.Telemetry;

/// <summary>
/// Provides a wrapper around the VSTelemetry histogram APIs to support aggregated telemetry. Each instance
/// of this class corresponds to a specific FunctionId operation and can support aggregated values for each
/// metric name logged.
/// </summary>
/// <remarks>
/// Creates a new aggregating telemetry log
/// </remarks>
/// <param name="session">Telemetry session used to post events</param>
/// <param name="functionId">Used to derive meter name</param>
internal sealed class AggregatingHistogramLog(TelemetrySession session, FunctionId functionId) : AbstractAggregatingLog<IHistogram<long>, long>(session, functionId), ITelemetryBlockLog
{
    public IDisposable? LogBlockTime(KeyValueLogMessage logMessage, int minThresholdMs)
    {
        if (!IsEnabled)
            return null;

        if (!logMessage.Properties.TryGetValue(TelemetryLogging.KeyName, out var nameValue) || nameValue is not string)
            throw ExceptionUtilities.Unreachable();

        return new TimedTelemetryLogBlock(logMessage, minThresholdMs, telemetryLog: this);
    }

    protected override IHistogram<long> CreateAggregator(IMeter meter, string metricName)
    {
        return meter.CreateHistogram<long>(metricName);
    }

    protected override void UpdateAggregator(IHistogram<long> histogram, long value)
    {
        histogram.Record(value);
    }

    protected override TelemetryMetricEvent CreateTelemetryEvent(TelemetryEvent telemetryEvent, IHistogram<long> histogram)
    {
        return new TelemetryHistogramEvent<long>(telemetryEvent, histogram);
    }
}
