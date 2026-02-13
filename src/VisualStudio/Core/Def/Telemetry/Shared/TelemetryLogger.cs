// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.VisualStudio.LanguageServices.Telemetry;
using Microsoft.VisualStudio.Telemetry;

namespace Microsoft.CodeAnalysis.Telemetry;

internal abstract class TelemetryLogger : AbstractTelemetryLogger<TelemetryEvent, object>
{
    private sealed class Implementation : TelemetryLogger
    {
        private readonly TelemetrySession _session;

        private Implementation(TelemetrySession session, bool logDelta)
        {
            _session = session;
            LogDelta = logDelta;
        }

        public static new Implementation Create(TelemetrySession session, bool logDelta)
        {
            var logger = new Implementation(session, logDelta);

            // Two stage initialization as TelemetryLogProvider.Create needs access to
            //  the ILogger that this class implements.
            TelemetryLogProvider.Create(session, logger);

            return logger;
        }

        protected override bool LogDelta { get; }

        public override bool IsEnabled(FunctionId functionId)
            => _session.IsOptedIn;

        protected override void PostEvent(TelemetryEvent telemetryEvent)
            => _session.PostEvent(telemetryEvent);

        protected override object BlockStart(string eventName, LogType type)
            => type switch
            {
                LogType.Trace => _session.StartOperation(eventName),
                LogType.UserAction => _session.StartUserTask(eventName),
                _ => throw ExceptionUtilities.UnexpectedValue(type),
            };

        protected override TelemetryEvent GetEndEvent(object scope)
            => scope switch
            {
                TelemetryScope<OperationEvent> operation => operation.EndEvent,
                TelemetryScope<UserTaskEvent> userTask => userTask.EndEvent,
                _ => throw ExceptionUtilities.UnexpectedValue(scope)
            };

        protected override void BlockEnd(object scope, bool cancelled)
        {
            var result = cancelled ? TelemetryResult.UserCancel : TelemetryResult.Success;

            if (scope is TelemetryScope<OperationEvent> operation)
                operation.End(result);
            else if (scope is TelemetryScope<UserTaskEvent> userTask)
                userTask.End(result);
            else
                throw ExceptionUtilities.UnexpectedValue(scope);
        }
    }

    public static TelemetryLogger Create(TelemetrySession session, bool logDelta)
        => Implementation.Create(session, logDelta);

    protected override TelemetryEvent CreateTelemetryEvent(string eventName)
    {
        return new TelemetryEvent(eventName);
    }

    protected override void AddProperty(string propertyName, object? value, TelemetryEvent telemetryEvent)
    {
        telemetryEvent.Properties.Add(propertyName, value);
    }

    protected override void AddProperties(string propertyName, IEnumerable<object?> items, TelemetryEvent telemetryEvent)
    {
        telemetryEvent.Properties.Add(propertyName, new TelemetryComplexProperty(items));
    }

    protected override object CreatePiiProperty(PiiValue value)
    {
        return new TelemetryPiiProperty(value.Value);
    }
}
