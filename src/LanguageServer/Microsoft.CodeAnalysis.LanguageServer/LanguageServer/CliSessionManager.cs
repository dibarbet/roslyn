// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.CodeAnalysis.LanguageServer.Daemon;
using Microsoft.CodeAnalysis.LanguageServer.HostWorkspace;
using Microsoft.CommonLanguageServerProtocol.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Composition;
using Nerdbank.Streams;
using StreamJsonRpc;
using LSP = Roslyn.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.LanguageServer;

internal sealed class CliSessionManager : IAsyncDisposable
{
    private const string WorkspaceSymbolCommand = "workspaceSymbol";
    private const int FoundExitCode = 0;
    private const int NotFoundExitCode = 1;

    private static readonly TimeSpan s_sessionIdleTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan s_evictionInterval = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions s_envelopeJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, SessionEntry> _sessions;
    private readonly NamedPipeDaemonConnectionSource _connectionSource;
    private readonly LanguageServerConnectionManager _connectionManager;
    private readonly ExportProvider _exportProvider;
    private readonly AbstractTypeRefResolver _typeRefResolver;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetimeSource = new();
    private readonly Task _evictionTask;

    private bool _disposed;

    public CliSessionManager(
        NamedPipeDaemonConnectionSource connectionSource,
        LanguageServerConnectionManager connectionManager,
        ExportProvider exportProvider,
        AbstractTypeRefResolver typeRefResolver,
        ILogger logger)
    {
        _connectionSource = connectionSource;
        _connectionManager = connectionManager;
        _exportProvider = exportProvider;
        _typeRefResolver = typeRefResolver;
        _logger = logger;
        _sessions = new Dictionary<string, SessionEntry>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        _evictionTask = EvictIdleSessionsAsync();
    }

    public async Task HandleConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
        {
            AutoFlush = true,
        };

        CliRequestEnvelope request;
        try
        {
            var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (requestLine is null)
                return;

            request = JsonSerializer.Deserialize<CliRequestEnvelope>(requestLine, s_envelopeJsonOptions)
                ?? throw new InvalidDataException("The CLI request envelope was empty.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            await WriteResponseAsync(
                writer,
                new CliResponseEnvelope { Kind = CliProtocol.ErrorResponseKind, Error = ex.Message },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TryNormalizeSessionRoot(request.SessionRoot, out var sessionRoot, out var validationError))
        {
            await WriteResponseAsync(
                writer,
                new CliResponseEnvelope { Kind = CliProtocol.ErrorResponseKind, Error = validationError },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TryParseWorkspaceSymbolCommand(request.Arguments, out var query, out var commandError))
        {
            await WriteResponseAsync(
                writer,
                new CliResponseEnvelope { Kind = CliProtocol.ErrorResponseKind, Error = commandError },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var (entry, reused) = await GetOrCreateEntryAsync(sessionRoot).ConfigureAwait(false);
        var progressChannel = Channel.CreateUnbounded<JsonElement>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        void ReportProgress(JsonElement progress)
            => progressChannel.Writer.TryWrite(progress);

        entry.ProgressHub.Progress += ReportProgress;
        var progressPump = PumpProgressAsync(progressChannel.Reader, writer, cancellationToken);

        try
        {
            var session = await entry.Session.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            var result = await session.InvokeWorkspaceSymbolAsync(query, cancellationToken).ConfigureAwait(false);
            var commandResult = ConvertWorkspaceSymbols(result, sessionRoot);

            progressChannel.Writer.TryComplete();
            await progressPump.ConfigureAwait(false);

            await WriteResponseAsync(
                writer,
                new CliResponseEnvelope
                {
                    Kind = CliProtocol.ResultResponseKind,
                    Result = commandResult.Result,
                    Reused = reused,
                    ExitCode = commandResult.ExitCode,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await RemoveUnusableEntryAsync(entry).ConfigureAwait(false);
            progressChannel.Writer.TryComplete();

            try
            {
                await progressPump.ConfigureAwait(false);
                await WriteResponseAsync(
                    writer,
                    new CliResponseEnvelope { Kind = CliProtocol.ErrorResponseKind, Error = ex.Message },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                _logger.LogWarning(ex, "CLI request failed after its client disconnected.");
            }
        }
        finally
        {
            entry.ProgressHub.Progress -= ReportProgress;
            progressChannel.Writer.TryComplete();
            ReleaseEntry(entry);
        }
    }

    private async ValueTask<(SessionEntry Entry, bool Reused)> GetOrCreateEntryAsync(string sessionRoot)
    {
        CliSession? deadSession = null;
        SessionEntry entry;
        bool reused;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            reused = _sessions.TryGetValue(sessionRoot, out var existingEntry);
            if (reused &&
                existingEntry!.Session.IsValueCreated &&
                (existingEntry.Session.Value.IsFaulted ||
                 existingEntry.Session.Value.IsCanceled ||
                 existingEntry.Session.Value is { IsCompletedSuccessfully: true, Result.IsAlive: false }))
            {
                _sessions.Remove(sessionRoot);
                if (existingEntry.Session.Value.IsCompletedSuccessfully)
                    deadSession = existingEntry.Session.Value.Result;

                reused = false;
            }

            if (!reused)
            {
                var progressHub = new ProgressHub();
                entry = new SessionEntry(
                    sessionRoot,
                    progressHub,
                    new Lazy<Task<CliSession>>(
                        () => CliSession.CreateAsync(
                            sessionRoot,
                            progressHub,
                            _connectionSource,
                            _connectionManager,
                            _exportProvider,
                            _typeRefResolver,
                            _logger,
                            _lifetimeSource.Token),
                        LazyThreadSafetyMode.ExecutionAndPublication));
                _sessions.Add(sessionRoot, entry);
            }
            else
            {
                entry = existingEntry!;
            }

            entry.ActiveRequestCount++;
            entry.LastUsedUtc = DateTime.UtcNow;
        }

        if (deadSession is not null)
            await deadSession.DisposeAsync().ConfigureAwait(false);

        return (entry, reused);
    }

    private void ReleaseEntry(SessionEntry entry)
    {
        lock (_gate)
        {
            entry.ActiveRequestCount--;
            entry.LastUsedUtc = DateTime.UtcNow;
        }
    }

    private async ValueTask RemoveUnusableEntryAsync(SessionEntry entry)
    {
        if (!entry.Session.IsValueCreated)
            return;

        var sessionTask = entry.Session.Value;
        var deadSession = sessionTask.IsCompletedSuccessfully && !sessionTask.Result.IsAlive
            ? sessionTask.Result
            : null;
        if (!sessionTask.IsFaulted && deadSession is null)
            return;

        lock (_gate)
        {
            if (_sessions.TryGetValue(entry.SessionRoot, out var currentEntry) && ReferenceEquals(currentEntry, entry))
                _sessions.Remove(entry.SessionRoot);
        }

        if (deadSession is not null)
            await deadSession.DisposeAsync().ConfigureAwait(false);
    }

    private async Task PumpProgressAsync(
        ChannelReader<JsonElement> progressReader,
        StreamWriter writer,
        CancellationToken cancellationToken)
    {
        await foreach (var progress in progressReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await WriteResponseAsync(
                writer,
                new CliResponseEnvelope { Kind = CliProtocol.ProgressResponseKind, Progress = progress },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task WriteResponseAsync(
        StreamWriter writer,
        CliResponseEnvelope response,
        CancellationToken cancellationToken)
    {
        var responseLine = JsonSerializer.Serialize(response, s_envelopeJsonOptions);
        return writer.WriteLineAsync(responseLine.AsMemory(), cancellationToken);
    }

    private async Task EvictIdleSessionsAsync()
    {
        using var timer = new PeriodicTimer(s_evictionInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(_lifetimeSource.Token).ConfigureAwait(false))
            {
                List<SessionEntry>? evictedEntries = null;
                var idleCutoffUtc = DateTime.UtcNow - s_sessionIdleTimeout;

                lock (_gate)
                {
                    foreach (var (sessionRoot, entry) in _sessions)
                    {
                        if (entry.ActiveRequestCount == 0 && entry.LastUsedUtc <= idleCutoffUtc)
                        {
                            evictedEntries ??= [];
                            evictedEntries.Add(entry);
                        }
                    }

                    if (evictedEntries is not null)
                    {
                        foreach (var entry in evictedEntries)
                            _sessions.Remove(entry.SessionRoot);
                    }
                }

                if (evictedEntries is not null)
                {
                    foreach (var entry in evictedEntries)
                        await DisposeEntryAsync(entry).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetimeSource.IsCancellationRequested)
        {
        }
    }

    private async Task DisposeEntryAsync(SessionEntry entry)
    {
        if (!entry.Session.IsValueCreated)
            return;

        try
        {
            var session = await entry.Session.Value.ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeSource.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispose CLI session for '{sessionRoot}'.", entry.SessionRoot);
        }
    }

    private static bool TryNormalizeSessionRoot(
        string sessionRoot,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? normalizedSessionRoot,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        try
        {
            normalizedSessionRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionRoot));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalizedSessionRoot = null;
            error = $"Invalid session root: {ex.Message}";
            return false;
        }

        if (!Directory.Exists(normalizedSessionRoot))
        {
            error = $"Session root does not exist: {normalizedSessionRoot}";
            normalizedSessionRoot = null;
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryParseWorkspaceSymbolCommand(
        string[] arguments,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? query,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        if (arguments.Length == 2 &&
            arguments[0] == WorkspaceSymbolCommand &&
            !string.IsNullOrWhiteSpace(arguments[1]))
        {
            query = arguments[1];
            error = null;
            return true;
        }

        query = null;
        error = "Usage: roslyn-language-server rls workspaceSymbol <query>";
        return false;
    }

    private static CliCommandResult ConvertWorkspaceSymbols(JsonElement? result, string sessionRoot)
    {
        if (result is not JsonElement resultElement ||
            resultElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new CliCommandResult(
                JsonSerializer.SerializeToElement(Array.Empty<CliWorkspaceSymbolResult>(), s_envelopeJsonOptions),
                NotFoundExitCode);
        }

        if (resultElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The workspace symbol response was not a JSON array.");

        var symbols = new List<CliWorkspaceSymbolResult>();
        foreach (var symbol in resultElement.EnumerateArray())
        {
            var location = symbol.GetProperty("location");
            var uri = location.GetProperty("uri").GetString()
                ?? throw new InvalidDataException("A workspace symbol result did not contain a URI.");
            var start = location.GetProperty("range").GetProperty("start");

            symbols.Add(new CliWorkspaceSymbolResult
            {
                Name = symbol.GetProperty("name").GetString()
                    ?? throw new InvalidDataException("A workspace symbol result did not contain a name."),
                Kind = symbol.GetProperty("kind").GetInt32(),
                Path = GetNativePath(uri, sessionRoot),
                Line = start.GetProperty("line").GetInt32() + 1,
                Column = start.GetProperty("character").GetInt32() + 1,
                ContainerName = symbol.TryGetProperty("containerName", out var containerName)
                    ? containerName.GetString()
                    : null,
            });
        }

        return new CliCommandResult(
            JsonSerializer.SerializeToElement(symbols, s_envelopeJsonOptions),
            symbols.Count == 0 ? NotFoundExitCode : FoundExitCode);
    }

    private static string GetNativePath(string uriText, string sessionRoot)
    {
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || !uri.IsFile)
            return uriText;

        var localPath = uri.LocalPath;
        if (OperatingSystem.IsWindows() &&
            localPath.Length >= 3 &&
            localPath[0] is '/' or '\\' &&
            localPath[2] == ':')
        {
            localPath = localPath[1..];
        }

        return Path.GetRelativePath(sessionRoot, Path.GetFullPath(localPath));
    }

    public async ValueTask DisposeAsync()
    {
        List<SessionEntry> entries;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            entries = [.. _sessions.Values];
            _sessions.Clear();
        }

        _lifetimeSource.Cancel();
        await _evictionTask.ConfigureAwait(false);

        foreach (var entry in entries)
            await DisposeEntryAsync(entry).ConfigureAwait(false);

        _lifetimeSource.Dispose();
    }

    private sealed class SessionEntry(
        string sessionRoot,
        ProgressHub progressHub,
        Lazy<Task<CliSession>> session)
    {
        public string SessionRoot { get; } = sessionRoot;
        public ProgressHub ProgressHub { get; } = progressHub;
        public Lazy<Task<CliSession>> Session { get; } = session;
        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
        public int ActiveRequestCount { get; set; }
    }

    private sealed class ProgressHub
    {
        public event Action<JsonElement>? Progress;

        public void Report(JsonElement progress)
            => Progress?.Invoke(progress.Clone());

        public void ReportPhase(string phase)
            => Report(JsonSerializer.SerializeToElement(new { phase }));
    }

    private sealed class CliSession : IAsyncDisposable
    {
        private static readonly TimeSpan s_projectLoadTimeout = TimeSpan.FromMinutes(10);

        private readonly string _sessionRoot;
        private readonly JsonRpc _clientRpc;
        private readonly LanguageServerConnection _connection;
        private readonly CliSessionLog _sessionLog;
        private readonly VirtualClientTarget _virtualClientTarget;

        private LanguageServerHost? _server;
        private Task? _serverExitTask;
        private int _disposed;

        private CliSession(
            string sessionRoot,
            ProgressHub progressHub,
            NamedPipeDaemonConnectionSource connectionSource,
            ILogger logger)
        {
            _sessionRoot = sessionRoot;
            _sessionLog = new CliSessionLog(sessionRoot);
            _virtualClientTarget = new VirtualClientTarget(progressHub, _sessionLog);

            var messageFormatter = RoslynLanguageServer.CreateJsonMessageFormatter();

            var (clientStream, serverStream) = FullDuplexStream.CreatePair();
            _clientRpc = new JsonRpc(
                new HeaderDelimitedMessageHandler(clientStream, clientStream, messageFormatter),
                _virtualClientTarget)
            {
                ExceptionStrategy = ExceptionProcessing.CommonErrorData,
            };

            try
            {
                _connection = connectionSource.CreateInProcessConnection(serverStream, serverStream, serverStream);
            }
            catch
            {
                _clientRpc.Dispose();
                _sessionLog.Dispose();
                throw;
            }
        }

        public static async Task<CliSession> CreateAsync(
            string sessionRoot,
            ProgressHub progressHub,
            NamedPipeDaemonConnectionSource connectionSource,
            LanguageServerConnectionManager connectionManager,
            ExportProvider exportProvider,
            AbstractTypeRefResolver typeRefResolver,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            CliSession? session = null;
            try
            {
                progressHub.ReportPhase("creatingSession");
                session = new CliSession(
                    sessionRoot,
                    progressHub,
                    connectionSource,
                    logger);
                session._server = await connectionManager.StartConnectionAsync(
                    session._connection,
                    exportProvider,
                    typeRefResolver,
                    logger,
                    isolateFaults: true,
                    cancellationToken).ConfigureAwait(false);
                session._serverExitTask = session._server.WaitForExitAsync();
                progressHub.ReportPhase("initializing");
                await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
                progressHub.ReportPhase("ready");
                return session;
            }
            catch
            {
                if (session is not null)
                    await session.DisposeAsync().ConfigureAwait(false);

                throw;
            }
        }

        private async Task InitializeAsync(CancellationToken cancellationToken)
        {
            _clientRpc.StartListening();

            var rootDocumentUri = ProtocolConversions.CreateAbsoluteDocumentUri(_sessionRoot);

#pragma warning disable CS0618 // RootDocumentUri is sent for compatibility alongside WorkspaceFolders.
            _ = await _clientRpc.InvokeWithParameterObjectAsync<LSP.InitializeResult>(
                LSP.Methods.InitializeName,
                new LSP.InitializeParams
                {
                    ProcessId = Environment.ProcessId,
                    RootDocumentUri = rootDocumentUri,
                    Capabilities = new LSP.ClientCapabilities
                    {
                        Window = new LSP.WindowClientCapabilities { WorkDoneProgress = true },
                    },
                    WorkspaceFolders =
                    [
                        new LSP.WorkspaceFolder
                        {
                            DocumentUri = rootDocumentUri,
                            Name = Path.GetFileName(_sessionRoot),
                        },
                    ],
                },
                cancellationToken).ConfigureAwait(false);
#pragma warning restore CS0618

            _virtualClientTarget.ReportPhase("initialized");
            _ = await _clientRpc.InvokeWithParameterObjectAsync<object?>(
                LSP.Methods.InitializedName,
                new LSP.InitializedParams(),
                cancellationToken).ConfigureAwait(false);

            _virtualClientTarget.ReportPhase("loadingProjects");
            await _virtualClientTarget.ProjectInitializationComplete
                .WaitAsync(s_projectLoadTimeout, cancellationToken)
                .ConfigureAwait(false);
        }

        public Task<JsonElement?> InvokeWorkspaceSymbolAsync(string query, CancellationToken cancellationToken)
        {
            return _clientRpc.InvokeWithParameterObjectAsync<JsonElement?>(
                LSP.Methods.WorkspaceSymbolName,
                new LSP.WorkspaceSymbolParams { Query = query },
                cancellationToken);
        }

        public bool IsAlive
            => !_clientRpc.Completion.IsCompleted && _serverExitTask is { IsCompleted: false };

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                _clientRpc.Dispose();

                if (_serverExitTask is not null)
                    await _serverExitTask.ConfigureAwait(false);
            }
            finally
            {
                _sessionLog.Dispose();
            }
        }
    }

    private readonly record struct CliCommandResult(JsonElement Result, int ExitCode);

    private sealed class CliWorkspaceSymbolResult
    {
        public required string Name { get; init; }
        public int Kind { get; init; }
        public required string Path { get; init; }
        public int Line { get; init; }
        public int Column { get; init; }
        public string? ContainerName { get; init; }
    }

    private sealed class CliSessionLog : IDisposable
    {
        private readonly object _gate = new();
        private readonly StreamWriter _writer;

        public CliSessionLog(string sessionRoot)
        {
            var logDirectory = Path.Combine(Path.GetTempPath(), "roslyn-language-server", "cli-sessions");
            Directory.CreateDirectory(logDirectory);

            var rootHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionRoot)))[..16];
            var logPath = Path.Combine(logDirectory, $"{rootHash}-{Environment.ProcessId}.log");
            _writer = new StreamWriter(new FileStream(
                logPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete))
            {
                AutoFlush = true,
            };
        }

        public void Write(JsonElement message)
        {
            lock (_gate)
                _writer.WriteLine($"{DateTime.UtcNow:O} {message.GetRawText()}");
        }

        public void Dispose()
        {
            lock (_gate)
                _writer.Dispose();
        }
    }

    private sealed class VirtualClientTarget(ProgressHub progressHub, CliSessionLog sessionLog)
    {
        private readonly TaskCompletionSource _projectInitializationComplete =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ProjectInitializationComplete => _projectInitializationComplete.Task;

        public void ReportPhase(string phase)
            => progressHub.ReportPhase(phase);

        [JsonRpcMethod(LSP.Methods.WindowWorkDoneProgressCreateName, UseSingleObjectParameterDeserialization = true)]
        public Task HandleCreateWorkDoneProgressAsync(JsonElement _, CancellationToken cancellationToken)
            => Task.CompletedTask;

        [JsonRpcMethod(LSP.Methods.ProgressNotificationName, UseSingleObjectParameterDeserialization = true)]
        public Task HandleProgressAsync(JsonElement progress, CancellationToken cancellationToken)
        {
            progressHub.Report(progress);
            return Task.CompletedTask;
        }

        [JsonRpcMethod(LSP.Methods.WindowLogMessageName, UseSingleObjectParameterDeserialization = true)]
        public Task HandleLogMessageAsync(JsonElement message, CancellationToken cancellationToken)
        {
            sessionLog.Write(message);
            return Task.CompletedTask;
        }

        [JsonRpcMethod(ProjectInitializationHandler.ProjectInitializationCompleteName)]
        public void HandleProjectInitializationComplete()
            => _projectInitializationComplete.TrySetResult();
    }
}
