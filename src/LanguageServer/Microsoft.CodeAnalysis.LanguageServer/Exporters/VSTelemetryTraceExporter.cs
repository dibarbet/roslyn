// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Microsoft.CodeAnalysis.Contracts.Telemetry;
using Microsoft.CodeAnalysis.Internal.Log;
using OpenTelemetry;

namespace Microsoft.CodeAnalysis.LanguageServer.Exporters;

/// <summary>
/// OTel trace exporter that forwards <c>Roslyn.Logger</c> activities to <see cref="ITelemetryReporter"/>.
/// <para>
/// Zero-duration activities (from <c>Logger.Log</c>) are reported as <see cref="ITelemetryReporter.Log"/>.
/// Activities with duration (from <c>Logger.LogBlock</c>) are reported as
/// <see cref="ITelemetryReporter.LogBlockStart"/>/<see cref="ITelemetryReporter.LogBlockEnd"/>.
/// </para>
/// Activities from other sources (e.g. <c>Roslyn.LanguageServer</c>) are skipped.
/// </summary>
internal sealed class VSTelemetryTraceExporter : BaseExporter<Activity>
{
    private readonly ITelemetryReporter _reporter;
    private int _nextBlockId;

    public VSTelemetryTraceExporter(ITelemetryReporter reporter)
    {
        _reporter = reporter;
    }

    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch)
        {
            if (activity.Source.Name != "Roslyn.Logger")
                continue;

            try
            {
                var eventName = activity.DisplayName;
                var functionIdTag = activity.GetTagItem("roslyn.function_id");
                var logTypeTag = activity.GetTagItem("roslyn.log_type");
                var blockIdTag = activity.GetTagItem("roslyn.block_id");

                if (blockIdTag is not null)
                {
                    // LogBlock: paired start/end
                    var kind = logTypeTag is int kindInt ? kindInt : (int)LogType.Trace;
                    var blockId = Interlocked.Increment(ref _nextBlockId);

                    _reporter.LogBlockStart(eventName, kind, blockId);

                    var properties = ExtractProperties(activity);
                    _reporter.LogBlockEnd(blockId, properties, CancellationToken.None);
                }
                else
                {
                    // Logger.Log: single event
                    var properties = ExtractProperties(activity);
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

    private static List<KeyValuePair<string, object?>> ExtractProperties(Activity activity)
    {
        var properties = new List<KeyValuePair<string, object?>>();
        var functionId = activity.GetTagItem("roslyn.function_id") is int fid ? (FunctionId)fid : default;

        foreach (var tag in activity.Tags)
        {
            // Skip internal routing tags
            if (tag.Key is "roslyn.function_id" or "roslyn.function_name" or "roslyn.log_type" or "roslyn.block_id")
                continue;

            if (tag.Key.StartsWith("roslyn.prop.", StringComparison.Ordinal))
            {
                var propName = tag.Key["roslyn.prop.".Length..];
                properties.Add(new(TelemetryNaming.GetPropertyName(functionId, propName), tag.Value));
            }
            else if (tag.Key == "roslyn.message")
            {
                properties.Add(new(TelemetryNaming.GetPropertyName(functionId, "Message"), tag.Value));
            }
            else if (tag.Key == "roslyn.delta")
            {
                properties.Add(new(TelemetryNaming.GetPropertyName(functionId, "Delta"), tag.Value));
            }
            else if (tag.Key == "roslyn.cancelled")
            {
                properties.Add(new(TelemetryNaming.GetPropertyName(functionId, "Cancelled"), tag.Value));
            }
        }

        return properties;
    }
}
