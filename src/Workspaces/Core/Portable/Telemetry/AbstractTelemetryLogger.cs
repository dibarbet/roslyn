// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.Internal.Log;

namespace Microsoft.CodeAnalysis.Telemetry;

internal abstract class AbstractTelemetryLogger<TTelemetryEvent, TScope> : ILogger
{

    private readonly ConcurrentDictionary<int, TScope> _pendingScopes = new(concurrencyLevel: 2, capacity: 10);

    protected abstract bool LogDelta { get; }

    public abstract bool IsEnabled(FunctionId functionId);
    protected abstract TScope BlockStart(string eventName, LogType type);
    protected abstract void BlockEnd(TScope scope, bool cancelled);
    protected abstract TTelemetryEvent GetEndEvent(TScope scope);

    protected abstract TTelemetryEvent CreateTelemetryEvent(string eventName);
    protected abstract void PostEvent(TTelemetryEvent telemetryEvent);
    protected abstract void AddProperty(string propertyName, object? value, TTelemetryEvent telemetryEvent);
    protected abstract void AddProperties(string propertyName, IEnumerable<object?> items, TTelemetryEvent telemetryEvent);
    protected abstract object CreatePiiProperty(PiiValue value);

    public void Log(FunctionId functionId, LogMessage logMessage)
    {
        if (IgnoreMessage(logMessage))
        {
            return;
        }

        var telemetryEvent = CreateTelemetryEvent(TelemetryNaming.GetEventName(functionId));
        SetProperties(telemetryEvent, functionId, logMessage);

        try
        {
            PostEvent(telemetryEvent);
        }
        catch
        {
        }
    }

    public void LogBlockStart(FunctionId functionId, LogMessage logMessage, int blockId, CancellationToken cancellationToken)
    {
        if (IgnoreMessage(logMessage))
        {
            return;
        }

        var eventName = TelemetryNaming.GetEventName(functionId);
        var kind = GetKind(logMessage);

        try
        {
            _pendingScopes[blockId] = BlockStart(eventName, kind);
        }
        catch
        {
        }
    }

    public void LogBlockEnd(FunctionId functionId, LogMessage logMessage, int blockId, int delta, CancellationToken cancellationToken)
    {
        if (IgnoreMessage(logMessage))
        {
            return;
        }

        Contract.ThrowIfFalse(_pendingScopes.TryRemove(blockId, out var scope));

        var endEvent = GetEndEvent(scope);
        SetProperties(endEvent, functionId, logMessage, LogDelta ? delta : null);

        try
        {
            BlockEnd(scope, cancellationToken.IsCancellationRequested);
        }
        catch
        {
        }
    }

    private static bool IgnoreMessage(LogMessage logMessage)
        => logMessage.LogLevel < LogLevel.Information;

    private static LogType GetKind(LogMessage logMessage)
        => logMessage is KeyValueLogMessage kvLogMessage
                            ? kvLogMessage.Kind
                            : logMessage.LogLevel switch
                            {
                                >= LogLevel.Information => LogType.UserAction,
                                _ => LogType.Trace
                            };

    private void SetProperties(TTelemetryEvent telemetryEvent, FunctionId functionId, LogMessage logMessage, int? delta = null)
    {
        if (logMessage is KeyValueLogMessage kvLogMessage)
        {
            AppendProperties(telemetryEvent, functionId, kvLogMessage);
        }
        else
        {
            var message = logMessage.GetMessage();
            if (!string.IsNullOrWhiteSpace(message))
            {
                var propertyName = TelemetryNaming.GetPropertyName(functionId, "Message");
                AddProperty(propertyName, message, telemetryEvent);
            }
        }

        if (delta.HasValue)
        {
            var propertyName = TelemetryNaming.GetPropertyName(functionId, "Delta");
            AddProperty(propertyName, delta.Value, telemetryEvent);
        }
    }

    private void AppendProperties(TTelemetryEvent telemetryEvent, FunctionId functionId, KeyValueLogMessage logMessage)
    {
        foreach (var (name, value) in logMessage.Properties)
        {
            // call SetProperty. VS telemetry will take care of finding correct
            // API based on given object type for us.
            // 
            // numeric data will show up in ES with measurement prefix.
            var propertyName = TelemetryNaming.GetPropertyName(functionId, name);
            switch (value)
            {
                case IEnumerable<object> items:
                    AddProperties(propertyName, items.Select(item => WrapIfPii(item)), telemetryEvent);
                    break;
                default:
                    AddProperty(TelemetryNaming.GetPropertyName(functionId, name), WrapIfPii(value), telemetryEvent);
                    break;
            }
        }

        object? WrapIfPii(object? value)
        {
            return value is PiiValue pii ? CreatePiiProperty(pii) : value;
        }
    }
}
