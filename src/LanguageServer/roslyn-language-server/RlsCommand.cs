// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis.LanguageServer.Daemon;

namespace Microsoft.CodeAnalysis.LanguageServer.Client;

internal static class RlsCommand
{
    private const int ErrorExitCode = 2;

    public static bool IsRequested(string[] args)
        => args.Length > 0 && args[0] == "rls";

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var sessionRoot = Path.GetFullPath(Directory.GetCurrentDirectory());
            var request = new CliRequestEnvelope
            {
                SessionRoot = sessionRoot,
                Arguments = args[1..],
            };

            var executable = ServerExecutable.ResolveLanguageServer();
            using var daemonResult = await DaemonClient.ConnectCliAsync(
                executable,
                ["--autoLoadProjects", "500"]).ConfigureAwait(false);

            if (!daemonResult.DaemonConnected)
            {
                Console.Error.WriteLine("Unable to connect to the language server daemon.");
                return ErrorExitCode;
            }

            return await ExecuteAsync(daemonResult.NamedPipeStream, request).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or InvalidOperationException or JsonException or TimeoutException)
        {
            Console.Error.WriteLine(ex.Message);
            return ErrorExitCode;
        }
    }

    private static async Task<int> ExecuteAsync(Stream stream, CliRequestEnvelope request)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
        {
            AutoFlush = true,
        };

        var requestLine = JsonSerializer.Serialize(request, typeof(CliRequestEnvelope), RlsJsonContext.Default);
        await writer.WriteLineAsync(requestLine).ConfigureAwait(false);

        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } responseLine)
        {
            var response = (CliResponseEnvelope?)JsonSerializer.Deserialize(
                responseLine,
                typeof(CliResponseEnvelope),
                RlsJsonContext.Default);

            if (response is null)
                throw new InvalidDataException("The daemon returned an empty CLI response.");

            switch (response.Kind)
            {
                case CliProtocol.ProgressResponseKind:
                    Console.Error.WriteLine(responseLine);
                    break;

                case CliProtocol.ErrorResponseKind:
                    Console.Error.WriteLine(response.Error ?? "The daemon returned an unspecified error.");
                    return ErrorExitCode;

                case CliProtocol.ResultResponseKind:
                    Console.Error.WriteLine($$"""{"phase":"session","reused":{{response.Reused.ToString().ToLowerInvariant()}}}""");
                    Console.Out.WriteLine(response.Result?.GetRawText() ?? "null");
                    return response.ExitCode ?? 0;

                default:
                    throw new InvalidDataException($"The daemon returned an unknown CLI response kind '{response.Kind}'.");
            }
        }

        throw new IOException("The daemon closed the CLI pipe before returning a result.");
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliRequestEnvelope))]
[JsonSerializable(typeof(CliResponseEnvelope))]
internal sealed partial class RlsJsonContext : JsonSerializerContext;
