// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.VisualStudio.Telemetry;
using Microsoft.VisualStudio.Telemetry.Metrics;
using Microsoft.VisualStudio.Telemetry.Metrics.Events;

namespace Microsoft.CodeAnalysis.Telemetry;

/// <summary>
/// Provides a wrapper around various VSTelemetry aggregating APIs to support aggregated telemetry. Each instance
/// of this class corresponds to a specific FunctionId operation and can support aggregated values for each
/// metric name logged.
/// </summary>
internal abstract class AbstractAggregatingLog<TAggregator, TValue> : AbstractAggregatingLog<TValue> where TAggregator : IInstrument
{
    private readonly IMeter _meter;
    private readonly TelemetrySession _session;
    private readonly object _flushLock;

    private ImmutableDictionary<string, (TAggregator aggregator, TelemetryEvent TelemetryEvent, object Lock)> _aggregations = ImmutableDictionary<string, (TAggregator, TelemetryEvent, object)>.Empty;

    /// <summary>
    /// Creates a new aggregating telemetry log
    /// </summary>
    /// <param name="session">Telemetry session used to post events</param>
    /// <param name="functionId">Used to derive meter name</param>
    public AbstractAggregatingLog(TelemetrySession session, FunctionId functionId) : base(functionId)
    {
        var meterProvider = new VSTelemetryMeterProvider();
        _session = session;
        _meter = meterProvider.CreateMeter(MeterName, version: MeterVersion);
        _flushLock = new();
    }

    protected override void UpdateAggregator(string name, KeyValueLogMessage logMessage, TValue value)
    {
        (var aggregator, _, var aggregatorLock) = ImmutableInterlocked.GetOrAdd(ref _aggregations, name, name =>
        {
            var telemetryEvent = new TelemetryEvent(EventName);
            var metricName = this.GetMetricNameAndUpdateProperties(name, logMessage, (propertyName, propertyValue) =>
            {
                telemetryEvent.Properties.Add(propertyName, propertyValue);
            });

            var aggregator = CreateAggregator(_meter, metricName);
            var aggregatorLock = new object();

            return (aggregator, telemetryEvent, aggregatorLock);
        });

        lock (aggregatorLock)
        {
            UpdateAggregator(aggregator, value);
        }
    }

    protected override bool IsEnabled => _session.IsOptedIn;

    protected abstract TAggregator CreateAggregator(IMeter meter, string metricName);

    protected abstract void UpdateAggregator(TAggregator aggregator, TValue value);

    protected abstract TelemetryMetricEvent CreateTelemetryEvent(TelemetryEvent telemetryEvent, TAggregator aggregator);

    public void Flush()
    {
        // This lock ensures that multiple calls to Flush cannot occur simultaneously.
        //  Without this lock, we would could potentially call PostMetricEvent multiple
        //  times for the same aggregation.
        lock (_flushLock)
        {
            foreach (var (aggregator, telemetryEvent, aggregatorLock) in _aggregations.Values)
            {
                // This fine-grained lock ensures that the aggregation isn't modified (via a Record call)
                //  during the creation of the TelemetryMetricEvent or the PostMetricEvent
                //  call that operates on it.
                lock (aggregatorLock)
                {
                    var aggregatorEvent = CreateTelemetryEvent(telemetryEvent, aggregator);
                    _session.PostMetricEvent(aggregatorEvent);
                }
            }

            _aggregations = ImmutableDictionary<string, (TAggregator, TelemetryEvent, object)>.Empty;
        }
    }
}
