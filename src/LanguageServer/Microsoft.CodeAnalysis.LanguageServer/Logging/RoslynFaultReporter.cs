// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Microsoft.CodeAnalysis.Common;
using Microsoft.CodeAnalysis.Contracts.Telemetry;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Internal.Log;

namespace Microsoft.CodeAnalysis.LanguageServer.Logging;

internal sealed class RoslynFaultReporter
{
    private static RoslynFaultReporter? _instance;
    private static ITelemetryReporter? _telemetryReporter;

    private RoslynFaultReporter()
    {
    }

    public static void Initialize(ITelemetryReporter? reporter, string? telemetryLevel, string? sessionId)
    {
        Contract.ThrowIfTrue(_instance is not null);

        FatalError.ErrorReporterHandler handler = ReportFault;
        FatalError.SetHandlers(handler, nonFatalHandler: handler);
        FatalError.CopyHandlersTo(typeof(Compilation).Assembly);

        if (reporter is not null && telemetryLevel is not null)
        {
            reporter.InitializeSession(telemetryLevel, sessionId, isDefaultSession: true);
            _telemetryReporter = reporter;
        }

        _instance = new();
    }

    private static void ReportFault(Exception exception, ErrorSeverity severity, bool forceDump)
    {
        try
        {
            if (exception is OperationCanceledException { InnerException: { } oceInnerException })
            {
                ReportFault(oceInnerException, severity, forceDump);
                return;
            }

            if (exception is AggregateException aggregateException)
            {
                // We (potentially) have multiple exceptions; let's just report each of them
                foreach (var innerException in aggregateException.Flatten().InnerExceptions)
                    ReportFault(innerException, severity, forceDump);

                return;
            }

            // Copy locally, as otherwise if we report a fault during shutdown we might also null reference (and then fatally crash the process)
            var telemetryReporter = _telemetryReporter;
            if (telemetryReporter is not null)
            {
                var eventName = TelemetryNaming.GetEventName(FunctionId.NonFatalWatson);
                var description = GetDescription(exception);
                var currentProcess = Process.GetCurrentProcess();
                telemetryReporter.ReportFault(eventName, description, (int)severity, forceDump, currentProcess.Id, exception);
            }
        }
        catch (OutOfMemoryException)
        {
            FailFast.OnFatalException(exception);
        }
        catch (Exception e)
        {
            FailFast.OnFatalException(e);
        }
    }

    public static void ShutdownAndReportSessionTelemetry()
    {
        if (_instance is null)
        {
            return;
        }

        FeaturesSessionTelemetry.Report();

        (var currentReporter, _telemetryReporter) = (_telemetryReporter, null);
        currentReporter?.Dispose();
        _instance = null;
    }

    private static string GetDescription(Exception exception)
    {
        const string CodeAnalysisNamespace = nameof(Microsoft) + "." + nameof(CodeAnalysis);

        try
        {
            var frames = new StackTrace(exception).GetFrames();
            if (frames != null)
            {
                foreach (var frame in frames)
                {
                    var method = frame?.GetMethod();
                    var methodName = method?.Name;
                    if (methodName == null)
                        continue;

                    var declaringTypeName = method?.DeclaringType?.FullName;
                    if (declaringTypeName == null)
                        continue;

                    if (!declaringTypeName.StartsWith(CodeAnalysisNamespace))
                        continue;

                    return declaringTypeName + "." + methodName;
                }
            }
        }
        catch
        {
        }

        return exception.Message;
    }
}
