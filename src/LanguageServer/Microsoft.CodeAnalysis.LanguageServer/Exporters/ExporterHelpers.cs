// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.Contracts.Telemetry;
using Microsoft.CodeAnalysis.LanguageServer.Exporters;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Roslyn.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.Logging;

internal static class OpenTelemetryHelpers
{
    private const int VSTelemetryExportIntervalMilliseconds = 60000;

    private static readonly ResourceBuilder s_resourceBuilder = ResourceBuilder.CreateDefault()
        .AddService(serviceName: OpenTelemetryConstants.ServiceName);

    public static TracerProvider InitializeTracerProvider(ITelemetryReporter? telemetryReporter)
    {
        var builder = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(s_resourceBuilder)
            .AddSource(OpenTelemetryConstants.RoslynLogger)
            .AddSource(OpenTelemetryConstants.LanguageServer);

        if (telemetryReporter is not null)
            builder.AddProcessor(new SimpleActivityExportProcessor(new VSTelemetryTraceExporter(telemetryReporter)));

        if (Environment.GetEnvironmentVariable("DOTNET_ROSLYN_OTLP_ENDPOINT") is { Length: > 0 } otlpEndpoint)
        {
#pragma warning disable RS0030 // OTLP endpoint is a network URI, not a file path
            builder.AddOtlpExporter(o => { o.Endpoint = new Uri(otlpEndpoint); o.Protocol = OtlpExportProtocol.Grpc; });
#pragma warning restore RS0030
        }

        return builder.Build();
    }

    public static MeterProvider InitializeMeterProvider(ITelemetryReporter? telemetryReporter)
    {
        var builder = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(s_resourceBuilder)
            .AddMeter(OpenTelemetryConstants.RoslynLogger);

        if (telemetryReporter is not null)
            builder.AddReader(new PeriodicExportingMetricReader(new VSTelemetryMetricExporter(telemetryReporter), exportIntervalMilliseconds: VSTelemetryExportIntervalMilliseconds));

        if (Environment.GetEnvironmentVariable("DOTNET_ROSLYN_OTLP_ENDPOINT") is { Length: > 0 } otlpEndpoint)
        {
#pragma warning disable RS0030 // OTLP endpoint is a network URI, not a file path
            builder.AddOtlpExporter(o => { o.Endpoint = new Uri(otlpEndpoint); o.Protocol = OtlpExportProtocol.Grpc; });
#pragma warning restore RS0030
        }

        return builder.Build();
    }
}