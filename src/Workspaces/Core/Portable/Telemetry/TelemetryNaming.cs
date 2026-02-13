// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;

namespace Microsoft.CodeAnalysis.Internal.Log;

/// <summary>
/// Shared helpers for mapping <see cref="FunctionId"/> values to telemetry event and property names.
/// Used by both VS TelemetryLogger and language server OTel exporters.
/// </summary>
internal static class TelemetryNaming
{
    public const string EventPrefix = "vs/ide/vbcs/";
    public const string PropertyPrefix = "vs.ide.vbcs.";

    private static readonly ConcurrentDictionary<FunctionId, string> s_eventMap = [];
    private static readonly ConcurrentDictionary<(FunctionId id, string name), string> s_propertyMap = [];

    public static string GetEventName(FunctionId id)
        => s_eventMap.GetOrAdd(id, id => EventPrefix + GetTelemetryName(id, separator: '/'));

    public static string GetPropertyName(FunctionId id, string name)
        => s_propertyMap.GetOrAdd((id, name), key => PropertyPrefix + GetTelemetryName(key.id, separator: '.') + "." + key.name.ToLowerInvariant());

    public static string GetTelemetryName(FunctionId id, char separator)
        => Enum.GetName(typeof(FunctionId), id)!.Replace('_', separator).ToLowerInvariant();

    public static LogType GetKind(LogMessage logMessage)
        => logMessage is KeyValueLogMessage kvLogMessage
            ? kvLogMessage.Kind
            : logMessage.LogLevel switch
            {
                >= LogLevel.Information => LogType.UserAction,
                _ => LogType.Trace
            };
}
