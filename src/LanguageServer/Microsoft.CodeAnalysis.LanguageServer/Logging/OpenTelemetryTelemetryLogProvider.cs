// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.Telemetry;
using OpenTelemetry.Metrics;

namespace Microsoft.CodeAnalysis.LanguageServer.Logging;

/// <summary>
/// Implements <see cref="ITelemetryLogProvider"/> backed by an OpenTelemetry <see cref="Meter"/>.
/// <para>
/// Histogram and counter logs from <see cref="TelemetryLogging"/> are mapped to OpenTelemetry instruments
/// </para>
/// </summary>
internal sealed class OpenTelemetryTelemetryLogProvider(MeterProvider meterProvider, OpenTelemetryRoslynLogger openTelemetryRoslynLogger) : ITelemetryLogProvider
{
    private ImmutableDictionary<FunctionId, OpenTelemetryHistogramLog> _histogramLogs = [];
    private ImmutableDictionary<FunctionId, OpenTelemetryCounterLog> _counterLogs = [];
    private ImmutableDictionary<FunctionId, TelemetryBlockLog> _blockLogs = [];

    public ITelemetryBlockLog? GetLog(FunctionId functionId)
        => ImmutableInterlocked.GetOrAdd(ref _blockLogs, functionId, fid => new TelemetryBlockLog(openTelemetryRoslynLogger, fid));

    public ITelemetryBlockLog? GetHistogramLog(FunctionId functionId, double[]? bucketBoundaries = null)
        => ImmutableInterlocked.GetOrAdd(ref _histogramLogs, functionId, static fid => new OpenTelemetryHistogramLog(fid));

    public ITelemetryLog? GetCounterLog(FunctionId functionId)
        => ImmutableInterlocked.GetOrAdd(ref _counterLogs, functionId, static fid => new OpenTelemetryCounterLog(fid));

    public void Flush()
        => meterProvider.ForceFlush();

    /// <summary>
    /// OpenTelemetry histogram-based log for aggregated duration metrics.
    /// </summary>
    private sealed class OpenTelemetryHistogramLog : AbstractAggregatingLog<long>, ITelemetryBlockLog
    {
        private Histogram<long>? _histogram;
        private readonly Meter _meter;

        public OpenTelemetryHistogramLog(FunctionId functionId) : base(functionId)
        {
            _meter = new Meter(MeterName, MeterVersion);
        }

        protected override void UpdateAggregator(string name, KeyValueLogMessage logMessage, long value)
        {
            var tagList = new TagList();
            var metricName = this.GetMetricNameAndUpdateProperties(name, logMessage, (propertyName, propertyValue) =>
            {
                tagList.Add(propertyName, propertyValue);
            });

            lock (this)
            {
                _histogram ??= _meter.CreateHistogram<long>(metricName, unit: "ms");
                _histogram.Record(value, tagList);
            }
        }

        protected override bool IsEnabled => true;

        public IDisposable? LogBlockTime(KeyValueLogMessage logMessage, int minThresholdMs)
        {
            return new TimedTelemetryLogBlock(logMessage, minThresholdMs, telemetryLog: this);
        }
    }

    /// <summary>
    /// OpenTelemetry counter-based log for aggregated count metrics.
    /// </summary>
    private sealed class OpenTelemetryCounterLog : AbstractAggregatingLog<long>
    {
        private readonly Meter _meter;
        private Counter<long>? _counter;

        public OpenTelemetryCounterLog(FunctionId functionId) : base(functionId)
        {
            _meter = new Meter(MeterName);
        }

        protected override void UpdateAggregator(string name, KeyValueLogMessage logMessage, long value)
        {
            var tagList = new TagList();
            var metricName = this.GetMetricNameAndUpdateProperties(name, logMessage, (propertyName, propertyValue) =>
            {
                tagList.Add(propertyName, propertyValue);
            });

            lock (this)
            {
                _counter ??= _meter.CreateCounter<long>(metricName);
                _counter.Add(value, tagList);
            }
        }

        protected override bool IsEnabled => true;
    }
}
