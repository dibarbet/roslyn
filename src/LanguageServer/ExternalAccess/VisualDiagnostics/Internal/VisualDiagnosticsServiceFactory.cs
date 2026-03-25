// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.ExternalAccess.VisualDiagnostics.Contracts;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.ServiceHub.Framework;
using Roslyn.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.ExternalAccess.VisualDiagnostics;

/// <summary>
/// LSP Service responsible for loading IVisualDiagnosticsLanguageService workspace service and delegate the broker service to the workspace service,
/// and handling MAUI XAML/C#/CSS/Razor Hot Reload support
/// </summary>
[ExportCSharpVisualBasicLspServiceFactory(typeof(OnInitializedService)), Shared]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
[method: ImportingConstructor]
internal sealed class VisualDiagnosticsServiceFactory(
    LspWorkspaceRegistrationService lspWorkspaceRegistrationService) : ILspServiceFactory
{
    private readonly LspWorkspaceRegistrationService _lspWorkspaceRegistrationService = lspWorkspaceRegistrationService;

    public ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind)
        => new OnInitializedService(_lspWorkspaceRegistrationService);

    private sealed class OnInitializedService(
        LspWorkspaceRegistrationService lspWorkspaceRegistrationService) : ILspService, IOnServiceBrokerInitialized, IDisposable
    {
        private readonly LspWorkspaceRegistrationService _lspWorkspaceRegistrationService = lspWorkspaceRegistrationService;
        private readonly CancellationTokenSource _disposalTokenSource = new();
        private IVisualDiagnosticsLanguageService? _visualDiagnosticsLanguageService;

        public void Dispose()
        {
            _disposalTokenSource.Cancel();
            (_visualDiagnosticsLanguageService as IDisposable)?.Dispose();
            _disposalTokenSource.Dispose();
        }

        /// <summary>
        /// <see cref="IOnServiceBrokerInitialized.OnServiceBrokerInitializedAsync(ILspServiceBrokerFactory, CancellationToken)"/> requires
        /// that LSP initialization has completed.
        /// </summary>
        /// <param name="serviceBroker"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public Task OnServiceBrokerInitializedAsync(ILspServiceBrokerFactory serviceBroker, CancellationToken cancellationToken)
        {
            return InitializeVisualDiagnosticsLanguageServiceAsync(serviceBroker.GetRequiredServiceBroker());
        }

        private async Task InitializeVisualDiagnosticsLanguageServiceAsync(IServiceBroker serviceBroker)
        {
            try
            {
                Workspace workspace = _lspWorkspaceRegistrationService.GetAllRegistrations().First(w => w.Kind == WorkspaceKind.Host);
                Contract.ThrowIfFalse(workspace != null, "We should always have a host workspace.");

                if (workspace.Services.GetService<IVisualDiagnosticsLanguageService>() is IVisualDiagnosticsLanguageService visualDiagnosticsLanguageService)
                {
                    await visualDiagnosticsLanguageService.InitializeAsync(serviceBroker, _disposalTokenSource.Token).ConfigureAwait(false);
                    _visualDiagnosticsLanguageService = visualDiagnosticsLanguageService;
                }
            }
            catch (OperationCanceledException) when (_disposalTokenSource.IsCancellationRequested)
            {
            }
        }
    }
}
