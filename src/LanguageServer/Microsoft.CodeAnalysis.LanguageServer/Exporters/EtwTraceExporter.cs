// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Microsoft.CodeAnalysis.Internal.Log;
using OpenTelemetry;

namespace Microsoft.CodeAnalysis.LanguageServer.Exporters;

/// <summary>
/// OTel trace exporter that forwards <c>Roslyn.Logger</c> activities to <see cref="RoslynEventSource"/> (ETW).
/// <para>
/// Zero-duration activities (from <c>Logger.Log</c>) are reported via <see cref="RoslynEventSource.Log"/>.
/// Activities with duration (from <c>Logger.LogBlock</c>) are reported via
/// <see cref="RoslynEventSource.BlockStart"/>/<see cref="RoslynEventSource.BlockStop"/>/<see cref="RoslynEventSource.BlockCanceled"/>.
/// </para>
/// Activities from other sources are skipped.
/// </summary>
internal sealed class EtwTraceExporter : BaseExporter<Activity>
{
    private readonly RoslynEventSource _source = RoslynEventSource.Instance;
    private int _nextBlockId;

    public override ExportResult Export(in Batch<Activity> batch)
    {
        if (!_source.IsEnabled())
            return ExportResult.Success;

        foreach (var activity in batch)
        {
            if (activity.Source.Name != "Roslyn.Logger")
                continue;

            try
            {
                var functionIdTag = activity.GetTagItem("roslyn.function_id");
                if (functionIdTag is not int functionIdInt)
                    continue;

                var functionId = (FunctionId)functionIdInt;
                var blockIdTag = activity.GetTagItem("roslyn.block_id");

                if (blockIdTag is not null)
                {
                    // LogBlock: emit BlockStart/BlockStop or BlockCanceled
                    var blockId = Interlocked.Increment(ref _nextBlockId);
                    var message = activity.GetTagItem("roslyn.message")?.ToString() ?? string.Empty;
                    var deltaTag = activity.GetTagItem("roslyn.delta");
                    var delta = deltaTag is string deltaStr && int.TryParse(deltaStr, out var d) ? d : (int)activity.Duration.TotalMilliseconds;
                    var cancelled = activity.GetTagItem("roslyn.cancelled")?.ToString() == "True";

                    _source.BlockStart(message, functionId, blockId);

                    if (cancelled)
                    {
                        _source.BlockCanceled(functionId, delta, blockId);
                    }
                    else
                    {
                        _source.BlockStop(functionId, delta, blockId);
                    }
                }
                else
                {
                    // Logger.Log: single event
                    var message = activity.GetTagItem("roslyn.message")?.ToString() ?? string.Empty;
                    _source.Log(message, functionId);
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
