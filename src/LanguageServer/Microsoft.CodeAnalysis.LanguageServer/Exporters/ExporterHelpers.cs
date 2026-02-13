// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.Contracts.Telemetry;
using Microsoft.CodeAnalysis.LanguageServer.Exporters;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Roslyn.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.Logging;

internal static class OpenTelemetryHelpers
{
    private const int VSTelemetryExportIntervalMilliseconds = 60000;

    public static TracerProvider InitializeTracerProvider(ITelemetryReporter? telemetryReporter)
    {
        var builder = Sdk.CreateTracerProviderBuilder()
            .AddSource(OpenTelemetryConstants.RoslynLogger)
            .AddSource(OpenTelemetryConstants.LanguageServer);

        if (telemetryReporter is not null)
            builder.AddProcessor(new SimpleActivityExportProcessor(new VSTelemetryTraceExporter(telemetryReporter)));

        return builder.Build();
    }

    public static MeterProvider InitializeMeterProvider(ITelemetryReporter? telemetryReporter)
    {
        var builder = Sdk.CreateMeterProviderBuilder()
            .AddMeter(OpenTelemetryConstants.RoslynLogger);

        if (telemetryReporter is not null)
            builder.AddReader(new PeriodicExportingMetricReader(new VSTelemetryMetricExporter(telemetryReporter), exportIntervalMilliseconds: VSTelemetryExportIntervalMilliseconds));

        return builder.Build();
    }
}