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
/// Histogram and counter logs from <see cref="TelemetryLogging"/> are mapped to OTel instruments
/// on the <c>"Roslyn.Logger"</c> meter so they flow through the OTel metrics pipeline.
/// </para>
/// </summary>
internal sealed class OTelTelemetryLogProvider : ITelemetryLogProvider
{
    internal static readonly Meter Meter = new("Roslyn.Logger");

    private readonly MeterProvider _meterProvider;
    private ImmutableDictionary<FunctionId, OTelHistogramLog> _histogramLogs = ImmutableDictionary<FunctionId, OTelHistogramLog>.Empty;
    private ImmutableDictionary<FunctionId, OTelCounterLog> _counterLogs = ImmutableDictionary<FunctionId, OTelCounterLog>.Empty;
    private ImmutableDictionary<FunctionId, OTelBlockLog> _blockLogs = ImmutableDictionary<FunctionId, OTelBlockLog>.Empty;

    public OTelTelemetryLogProvider(MeterProvider meterProvider)
    {
        _meterProvider = meterProvider;
    }

    public ITelemetryBlockLog? GetLog(FunctionId functionId)
        => ImmutableInterlocked.GetOrAdd(ref _blockLogs, functionId, static fid => new OTelBlockLog(fid));

    public ITelemetryBlockLog? GetHistogramLog(FunctionId functionId, double[]? bucketBoundaries = null)
        => ImmutableInterlocked.GetOrAdd(ref _histogramLogs, functionId, static fid => new OTelHistogramLog(fid));

    public ITelemetryLog? GetCounterLog(FunctionId functionId)
        => ImmutableInterlocked.GetOrAdd(ref _counterLogs, functionId, static fid => new OTelCounterLog(fid));

    public void Flush()
        => _meterProvider.ForceFlush();

    /// <summary>
    /// OTel histogram-based log for aggregated duration metrics.
    /// </summary>
    private sealed class OTelHistogramLog : ITelemetryBlockLog
    {
        private readonly Histogram<long> _histogram;

        public OTelHistogramLog(FunctionId functionId)
        {
            var name = TelemetryNaming.GetTelemetryName(functionId, separator: '.');
            _histogram = Meter.CreateHistogram<long>($"roslyn.{name}", unit: "ms");
        }

        public void Log(KeyValueLogMessage logMessage)
        {
            if (logMessage.Properties.TryGetValue(TelemetryLogging.KeyValue, out var value) && value is long longValue)
            {
                var tags = ExtractTags(logMessage);
                _histogram.Record(longValue, tags);
            }
        }

        public IDisposable? LogBlockTime(KeyValueLogMessage logMessage, int minThresholdMs = -1)
            => new BlockTimeLogger(_histogram, logMessage, minThresholdMs);
    }

    /// <summary>
    /// OTel counter-based log for aggregated count metrics.
    /// </summary>
    private sealed class OTelCounterLog : ITelemetryLog
    {
        private readonly Counter<long> _counter;

        public OTelCounterLog(FunctionId functionId)
        {
            var name = TelemetryNaming.GetTelemetryName(functionId, separator: '.');
            _counter = Meter.CreateCounter<long>($"roslyn.{name}");
        }

        public void Log(KeyValueLogMessage logMessage)
        {
            var increment = 1L;
            if (logMessage.Properties.TryGetValue(TelemetryLogging.KeyValue, out var value) && value is long longValue)
            {
                increment = longValue;
            }

            var tags = ExtractTags(logMessage);
            _counter.Add(increment, tags);
        }
    }

    /// <summary>
    /// OTel-backed block log for non-aggregated telemetry events.
    /// </summary>
    private sealed class OTelBlockLog : ITelemetryBlockLog
    {
        private readonly Histogram<long> _histogram;

        public OTelBlockLog(FunctionId functionId)
        {
            var name = TelemetryNaming.GetTelemetryName(functionId, separator: '.');
            _histogram = Meter.CreateHistogram<long>($"roslyn.{name}.block", unit: "ms");
        }

        public void Log(KeyValueLogMessage logMessage)
        {
            if (logMessage.Properties.TryGetValue(TelemetryLogging.KeyValue, out var value) && value is long longValue)
            {
                var tags = ExtractTags(logMessage);
                _histogram.Record(longValue, tags);
            }
        }

        public IDisposable? LogBlockTime(KeyValueLogMessage logMessage, int minThresholdMs = -1)
            => new BlockTimeLogger(_histogram, logMessage, minThresholdMs);
    }

    private sealed class BlockTimeLogger : IDisposable
    {
        private readonly Histogram<long> _histogram;
        private readonly KeyValueLogMessage _logMessage;
        private readonly int _minThresholdMs;
        private readonly long _startTicks;

        public BlockTimeLogger(Histogram<long> histogram, KeyValueLogMessage logMessage, int minThresholdMs)
        {
            _histogram = histogram;
            _logMessage = logMessage;
            _minThresholdMs = minThresholdMs;
            _startTicks = Environment.TickCount64;
        }

        public void Dispose()
        {
            var elapsed = Environment.TickCount64 - _startTicks;
            if (_minThresholdMs >= 0 && elapsed < _minThresholdMs)
            {
                _logMessage.Free();
                return;
            }

            var tags = ExtractTags(_logMessage);
            _histogram.Record(elapsed, tags);
            _logMessage.Free();
        }
    }

    private static TagList ExtractTags(KeyValueLogMessage logMessage)
    {
        var tags = new TagList();
        foreach (var (name, val) in logMessage.Properties)
        {
            if (name == TelemetryLogging.KeyValue)
                continue;

            tags.Add(name, val?.ToString());
        }

        return tags;
    }
}
