// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.Contracts.Telemetry;
using Microsoft.CodeAnalysis.Internal.Log;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Microsoft.CodeAnalysis.LanguageServer.Exporters;

/// <summary>
/// OTel metric exporter that forwards <c>Roslyn.Logger</c> meter metrics to <see cref="ITelemetryReporter"/>.
/// Aggregated histograms and counters from <see cref="TelemetryLogging"/> are forwarded with
/// the <c>vs/ide/vbcs/</c> naming convention.
/// </summary>
internal sealed class VSTelemetryMetricExporter : BaseExporter<Metric>
{
    private readonly ITelemetryReporter _reporter;

    public VSTelemetryMetricExporter(ITelemetryReporter reporter)
    {
        _reporter = reporter;
    }

    public override ExportResult Export(in Batch<Metric> batch)
    {
        foreach (var metric in batch)
        {
            try
            {
                var eventName = TelemetryNaming.EventPrefix + metric.Name.Replace('.', '/');

                foreach (var metricPoint in metric.GetMetricPoints())
                {
                    var properties = new List<KeyValuePair<string, object?>>();

                    // Add tags as properties
                    foreach (var tag in metricPoint.Tags)
                    {
                        properties.Add(new(TelemetryNaming.PropertyPrefix + metric.Name.Replace('.', '.') + "." + tag.Key.ToLowerInvariant(), tag.Value));
                    }

                    if (metric.MetricType.IsHistogram())
                    {
                        properties.Add(new(TelemetryNaming.PropertyPrefix + metric.Name + ".count", metricPoint.GetHistogramCount()));
                        properties.Add(new(TelemetryNaming.PropertyPrefix + metric.Name + ".sum", metricPoint.GetHistogramSum()));
                    }
                    else if (metric.MetricType.IsLong())
                    {
                        properties.Add(new(TelemetryNaming.PropertyPrefix + metric.Name + ".value", metricPoint.GetSumLong()));
                    }

                    _reporter.Log(eventName, properties);
                }
            }
            catch
            {
                // Don't let exporter errors crash the pipeline
            }
        }

        return ExportResult.Success;
    }
}
