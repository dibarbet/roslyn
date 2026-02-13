// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.Contracts.Telemetry;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Microsoft.CodeAnalysis.LanguageServer.Exporters;

/// <summary>
/// OpenTelemetry metric exporter that forwards <c>Roslyn.Logger</c> meter metrics to <see cref="ITelemetryReporter"/>.
/// These generally come from histograms and counters from <see cref="ITelemetryLogProvider"/>
/// </summary>
internal sealed class VSTelemetryMetricExporter(ITelemetryReporter reporter) : BaseExporter<Metric>
{
    public override ExportResult Export(in Batch<Metric> batch)
    {
        foreach (var metric in batch)
        {
            try
            {
                var eventName = TelemetryNaming.EventPrefix + metric.Name.Replace('.', '/');

                foreach (var metricPoint in metric.GetMetricPoints())
                {
                    reporter.LogMetric(metric);
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
