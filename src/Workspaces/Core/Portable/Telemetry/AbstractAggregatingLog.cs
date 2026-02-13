// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Internal.Log;

namespace Microsoft.CodeAnalysis.Telemetry;

/// <summary>
/// Provides a wrapper to support aggregated telemetry. Each instance
/// of this class corresponds to a specific FunctionId operation and can support aggregated values for each
/// metric name logged.
/// </summary>
internal abstract class AbstractAggregatingLog<TValue> : ITelemetryLog
{
    // Indicates version information which vs telemetry will use for our aggregated telemetry. This can be used
    // by Kusto queries to filter against telemetry versions which have the specified version and thus desired shape.
    protected const string MeterVersion = "0.40";

    protected readonly string MeterName;
    protected readonly string EventName;
    protected readonly FunctionId FunctionId;

    /// <summary>
    /// Creates a new aggregating telemetry log
    /// </summary>
    /// <param name="functionId">Used to derive meter name</param>
    public AbstractAggregatingLog(FunctionId functionId)
    {
        MeterName = TelemetryNaming.GetPropertyName(functionId, "meter");
        EventName = TelemetryNaming.GetEventName(functionId);
        FunctionId = functionId;
    }

    /// <summary>
    /// Adds aggregated information for the metric and value passed in via <paramref name="logMessage"/>. The Name/Value properties
    /// are used as the metric name and value to record.
    /// </summary>
    /// <param name="logMessage"></param>
    public void Log(KeyValueLogMessage logMessage)
    {
        if (!IsEnabled)
            return;

        // Name is the key for this message in our aggregation dictionary. It is also used as the metric name
        // if the MetricName property isn't specified.
        if (!logMessage.Properties.TryGetValue(TelemetryLogging.KeyName, out var nameValue) || nameValue is not string name)
            throw ExceptionUtilities.Unreachable();

        if (!logMessage.Properties.TryGetValue(TelemetryLogging.KeyValue, out var valueValue) || valueValue is not TValue value)
            throw ExceptionUtilities.Unreachable();

        UpdateAggregator(name, logMessage, value);
    }

    protected string GetMetricNameAndUpdateProperties(string name, KeyValueLogMessage logMessage, Action<string, object?> addProperty)
    {
        // For aggregated telemetry, the first Log request that comes in for a particular name determines the additional
        // properties added for the telemetry event.
        if (!logMessage.Properties.TryGetValue(TelemetryLogging.KeyMetricName, out var metricNameValue) || metricNameValue is not string metricName)
            metricName = name;

        foreach (var kvp in logMessage.Properties)
        {
            var curName = kvp.Key;
            var curValue = kvp.Value;

            if (curName is not TelemetryLogging.KeyName and not TelemetryLogging.KeyValue and not TelemetryLogging.KeyMetricName)
            {
                var propertyName = TelemetryNaming.GetPropertyName(FunctionId, curName);
                addProperty(propertyName, curValue);
            }
        }

        return metricName;
    }

    protected abstract void UpdateAggregator(string name, KeyValueLogMessage logMessage, TValue value);

    protected abstract bool IsEnabled { get; }
}
