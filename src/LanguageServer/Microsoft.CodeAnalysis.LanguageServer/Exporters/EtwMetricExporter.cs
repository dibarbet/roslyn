// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.Internal.Log;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Microsoft.CodeAnalysis.LanguageServer.Exporters;

/// <summary>
/// OTel metric exporter that forwards <c>Roslyn.Logger</c> meter metrics to <see cref="RoslynEventSource"/> (ETW).
/// </summary>
internal sealed class EtwMetricExporter : BaseExporter<Metric>
{
    private readonly RoslynEventSource _source = RoslynEventSource.Instance;

    public override ExportResult Export(in Batch<Metric> batch)
    {
        if (!_source.IsEnabled())
            return ExportResult.Success;

        foreach (var metric in batch)
        {
            try
            {
                foreach (var metricPoint in metric.GetMetricPoints())
                {
                    // ETW logging for aggregated metrics is informational - log as a summary event
                    var message = metric.MetricType.IsHistogram()
                        ? $"count={metricPoint.GetHistogramCount()} sum={metricPoint.GetHistogramSum()}"
                        : $"value={metricPoint.GetSumLong()}";

                    _source.Log($"{metric.Name}: {message}", FunctionId.TestEvent_NotUsed);
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
