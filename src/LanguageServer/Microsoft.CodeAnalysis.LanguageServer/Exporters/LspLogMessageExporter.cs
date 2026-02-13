// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.LanguageServer.LanguageServer;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Roslyn.LanguageServer.Protocol;
using StreamJsonRpc;

namespace Microsoft.CodeAnalysis.LanguageServer.Exporters;

/// <summary>
/// OTel log exporter that sends formatted log records to the VS Code output window
/// via LSP <c>window/logMessage</c> notifications.
/// <para>
/// Replaces <c>LspLogMessageLoggerProvider</c>. All M.E.Logging logs flow through
/// OTel's log pipeline and this exporter routes them to the LSP client.
/// </para>
/// </summary>
internal sealed class LspLogMessageExporter : BaseExporter<LogRecord>
{
    private readonly ServerConfiguration _serverConfiguration;

    public LspLogMessageExporter(ServerConfiguration serverConfiguration)
    {
        _serverConfiguration = serverConfiguration;
    }

    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        foreach (var logRecord in batch)
        {
            try
            {
                if (logRecord.LogLevel == LogLevel.None)
                    continue;

                if (_serverConfiguration.LogConfiguration.GetLogLevel() > logRecord.LogLevel)
                    continue;

                var message = logRecord.FormattedMessage ?? logRecord.Body ?? string.Empty;

                if (logRecord.Exception is not null)
                {
                    var exceptionString = logRecord.Exception.ToString();
                    if (message == "[null]")
                        message = exceptionString;
                    else
                        message += " " + exceptionString;
                }

                var categoryName = logRecord.CategoryName ?? "Unknown";
                var messagePrefix = $"[{categoryName}]";

                var logMethod = Methods.WindowLogMessageName;
                var formattedMessage = $"{messagePrefix} {message}";

                var server = LanguageServerHost.Instance;
                if (server == null)
                {
                    // Before server initialization, write to stderr as a fallback.
                    Console.Error.WriteLine(formattedMessage);
                    continue;
                }

                var _ = server.GetRequiredLspService<IClientLanguageServerManager>().SendNotificationAsync(logMethod, new LogMessageParams()
                {
                    Message = formattedMessage,
                    MessageType = LogLevelToMessageType(logRecord.LogLevel),
                }, CancellationToken.None);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or ConnectionLostException)
            {
                // Shutting down - connection lost. Safe to ignore.
            }
            catch (Exception ex) when (FatalError.ReportAndCatch(ex))
            {
                // Don't let exporter errors crash the pipeline
            }
        }

        return ExportResult.Success;
    }

    private static MessageType LogLevelToMessageType(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.Trace => MessageType.Debug,
            LogLevel.Debug => MessageType.Debug,
            LogLevel.Information => MessageType.Info,
            LogLevel.Warning => MessageType.Warning,
            LogLevel.Error => MessageType.Error,
            LogLevel.Critical => MessageType.Error,
            _ => throw ExceptionUtilities.UnexpectedValue(logLevel),
        };
    }
}
