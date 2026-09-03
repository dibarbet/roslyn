// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.CodeAnalysis.LanguageServer.Daemon;

internal static class CliProtocol
{
    public const string ProgressResponseKind = "progress";
    public const string ResultResponseKind = "result";
    public const string ErrorResponseKind = "error";
}

internal sealed class CliRequestEnvelope
{
    [JsonPropertyName("sessionRoot")]
    public required string SessionRoot { get; init; }

    [JsonPropertyName("arguments")]
    public required string[] Arguments { get; init; }
}

internal sealed class CliResponseEnvelope
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; init; }

    [JsonPropertyName("progress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Progress { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    [JsonPropertyName("reused")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Reused { get; init; }

    [JsonPropertyName("exitCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExitCode { get; init; }
}
