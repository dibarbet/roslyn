// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.Contracts.Telemetry;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.LanguageServer.Exporters;
using Microsoft.CodeAnalysis.LanguageServer.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Logging;
using Microsoft.CodeAnalysis.Telemetry;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.UnitTests;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Composition;
using Nerdbank.Streams;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Roslyn.LanguageServer.Protocol;
using StreamJsonRpc;
using Xunit.Abstractions;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests;

public abstract class AbstractLanguageServerHostTests : IDisposable
{
    protected ILoggerFactory LoggerFactory { get; }
    protected TempRoot TempRoot { get; }
    protected TempDirectory MefCacheDirectory { get; }

    protected AbstractLanguageServerHostTests(ITestOutputHelper testOutputHelper)
    {
        // Create a ServerConfiguration for the LspLogMessageExporter.
        var serverConfiguration = new ServerConfiguration(
            LaunchDebugger: false,
            LogConfiguration: new LogConfiguration(Microsoft.Extensions.Logging.LogLevel.Trace),
            StarredCompletionsPath: null,
            TelemetryLevel: null,
            SessionId: null,
            ExtensionAssemblyPaths: [],
            DevKitDependencyPath: null,
            RazorDesignTimePath: null,
            CSharpDesignTimePath: null,
            ExtensionLogDirectory: string.Empty,
            ServerPipeName: null,
            UseStdIo: false,
            AutoLoadProjects: false,
            SourceGeneratorExecutionPreference: Host.SourceGeneratorExecutionPreference.Balanced,
            ClientProcessId: null);

        // Wire up LoggerFactory with both OpenTelemetry (for LspLogMessageExporter) and TestOutput (for test diagnostics).
        var lspLogMessageExporter = new LspLogMessageExporter(serverConfiguration);
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);
            builder.AddProvider(new TestOutputLoggerProvider(testOutputHelper));
            builder.AddOpenTelemetry(options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
                options.AddProcessor(new SimpleLogRecordExportProcessor(lspLogMessageExporter));
            });
        });

        TempRoot = new();
        MefCacheDirectory = TempRoot.CreateDirectory();
    }

    private protected Task<TestLspServer> CreateLanguageServerAsync(
        ClientCapabilities? clientCapabilities = null,
        bool includeDevKitComponents = true,
        string[]? extensionPaths = null,
        ITelemetryReporter? telemetryReporter = null)
    {
        return TestLspServer.CreateAsync(clientCapabilities ?? new ClientCapabilities(), LoggerFactory, MefCacheDirectory.Path, includeDevKitComponents, extensionPaths, telemetryReporter);
    }

    public void Dispose()
    {
        LoggerFactory.Dispose();
        TempRoot.Dispose();
    }

    protected sealed class TestLspServer : ILspClient, IAsyncDisposable
    {
        private readonly Task _languageServerHostCompletionTask;
        private readonly JsonRpc _clientRpc;
        private readonly Stream _serverStream;
        private readonly Stream _clientStream;
        private readonly TracerProvider _tracerProvider;
        private readonly MeterProvider _meterProvider;

        internal static async Task<TestLspServer> CreateAsync(ClientCapabilities clientCapabilities, ILoggerFactory loggerFactory, string cacheDirectory, bool includeDevKitComponents = true, string[]? extensionPaths = null, ITelemetryReporter? telemetryReporter = null)
        {
            var (exportProvider, assemblyLoader) = await LanguageServerTestComposition.CreateExportProviderAsync(
                loggerFactory, includeDevKitComponents, cacheDirectory, extensionPaths);
            var testLspServer = new TestLspServer(exportProvider, loggerFactory, assemblyLoader, telemetryReporter);
            var initializeResponse = await testLspServer.ExecuteRequestAsync<InitializeParams, InitializeResult>(Methods.InitializeName, new InitializeParams { Capabilities = clientCapabilities }, CancellationToken.None);
            Assert.NotNull(initializeResponse?.Capabilities);
            testLspServer.ServerCapabilities = initializeResponse.Capabilities;

            await testLspServer.ExecuteRequestAsync<InitializedParams, object>(Methods.InitializedName, new InitializedParams(), CancellationToken.None);

            return testLspServer;
        }

        internal LanguageServerHost LanguageServerHost { get; }
        public ExportProvider ExportProvider { get; }

        internal ServerCapabilities ServerCapabilities { get => field ?? throw new InvalidOperationException("Initialize has not been called"); private set; }

        private TestLspServer(ExportProvider exportProvider, ILoggerFactory loggerFactory, IAssemblyLoader assemblyLoader, ITelemetryReporter? telemetryReporter = null)
        {
            var typeRefResolver = new ExtensionTypeRefResolver(assemblyLoader, loggerFactory);

            _tracerProvider = OpenTelemetryHelpers.InitializeTracerProvider(telemetryReporter);
            _meterProvider = OpenTelemetryHelpers.InitializeMeterProvider(telemetryReporter);

            Logger.SetLogger(new OpenTelemetryRoslynLogger());
            TelemetryLogging.SetLogProvider(new OpenTelemetryTelemetryLogProvider(_meterProvider));

            var (clientStream, serverStream) = FullDuplexStream.CreatePair();
            _serverStream = serverStream;
            _clientStream = clientStream;
            LanguageServerHost = new LanguageServerHost(serverStream, serverStream, exportProvider, loggerFactory, typeRefResolver);

            var messageFormatter = RoslynLanguageServer.CreateJsonMessageFormatter();
            _clientRpc = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, messageFormatter))
            {
                AllowModificationWhileListening = true,
                ExceptionStrategy = ExceptionProcessing.ISerializable,
            };

            _clientRpc.StartListening();

            // This task completes when the server shuts down.  We store it so that we can wait for completion
            // when we dispose of the test server.
            LanguageServerHost.Start();

            _languageServerHostCompletionTask = LanguageServerHost.WaitForExitAsync();
            ExportProvider = exportProvider;
        }

        public Task ServerExitTask => _languageServerHostCompletionTask;

        /// <summary>
        /// Simulates the transport layer failing by closing the server's stream.
        /// This forces an exception in the JsonRpc read loop.
        /// </summary>
        public void SimulateStreamReadError()
        {
            _serverStream.Close();
        }

        /// <summary>
        /// Simulates the client disconnecting abruptly by closing the client stream.
        /// </summary>
        public void SimulateClientDisconnectError()
        {
            _clientStream.Close();
        }

        public async Task<TResponseType?> ExecuteRequestAsync<TRequestType, TResponseType>(string methodName, TRequestType request, CancellationToken cancellationToken) where TRequestType : class
        {
            var result = await _clientRpc.InvokeWithParameterObjectAsync<TResponseType>(methodName, request, cancellationToken: cancellationToken);
            return result;
        }

        public Task ExecuteNotificationAsync<RequestType>(string methodName, RequestType request) where RequestType : class
        {
            return _clientRpc.NotifyWithParameterObjectAsync(methodName, request);
        }

        public Task ExecuteNotification0Async(string methodName)
        {
            return _clientRpc.NotifyWithParameterObjectAsync(methodName);
        }

        public void AddClientLocalRpcTarget(object target)
        {
            _clientRpc.AddLocalRpcTarget(target);
        }

        public void AddClientLocalRpcTarget(string methodName, Delegate handler)
        {
            _clientRpc.AddLocalRpcMethod(methodName, handler);
        }

        public async ValueTask DisposeAsync()
        {
            await _clientRpc.InvokeAsync(Methods.ShutdownName);
            await _clientRpc.NotifyAsync(Methods.ExitName);

            // The language server host task should complete once shutdown and exit are called.
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
            await _languageServerHostCompletionTask;
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks

            _clientRpc.Dispose();
            _tracerProvider?.Dispose();
            _meterProvider?.Dispose();
        }
    }
}
