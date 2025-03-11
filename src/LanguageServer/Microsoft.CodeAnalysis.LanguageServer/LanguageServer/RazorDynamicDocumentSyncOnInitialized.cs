// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Composition;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.Options;
using Roslyn.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.LanguageServer;

[ExportCSharpVisualBasicLspService(typeof(RazorDynamicDocumentSyncOnInitialized)), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal class RazorDynamicDocumentSyncOnInitialized(IGlobalOptionService globalOptionService) : IOnInitialized, ILspService
{
    public async Task OnInitializedAsync(ClientCapabilities clientCapabilities, RequestContext context, CancellationToken cancellationToken)
    {
        // Hot reload only works in devkit scenarios. Without, there is no need to register for dynamic document sync.
        if (!globalOptionService.GetOption(LspOptionsStorage.LspUsingDevkitFeatures))
        {
            return;
        }

        // If dynamic registration for text document synchronization is supported, register for .razor and .cshtml files
        // so that they are up to date for hot reload scenarios rather than depending on the file watchers to update
        // the contents.
        if (clientCapabilities.TextDocument?.Synchronization?.DynamicRegistration is true)
        {
            var languageServerManager = context.GetRequiredLspService<IClientLanguageServerManager>();

            var documentFilters = new[] { new DocumentFilter() { Pattern = "**/*.{razor, cshtml}", Language = "aspnetcorerazor" } };
            var registrationOptions = new TextDocumentRegistrationOptions()
            {
                DocumentSelector = documentFilters
            };

            await languageServerManager.SendRequestAsync(Methods.ClientRegisterCapabilityName,
                new RegistrationParams()
                {
                    Registrations = [
                        new()
                        {
                            Id = Guid.NewGuid().ToString(), // No need to save this for unregistering
                            Method = Methods.TextDocumentDidOpenName,
                            RegisterOptions = registrationOptions
                        },
                        new()
                        {
                            Id = Guid.NewGuid().ToString(), // No need to save this for unregistering
                            Method = Methods.TextDocumentDidChangeName,
                            RegisterOptions = new TextDocumentChangeRegistrationOptions()
                            {
                                DocumentSelector = documentFilters,
                                SyncKind = TextDocumentSyncKind.Incremental
                            }
                        },
                        new()
                        {
                            Id = Guid.NewGuid().ToString(), // No need to save this for unregistering
                            Method = Methods.TextDocumentDidCloseName,
                            RegisterOptions = registrationOptions
                        }
                    ]
                },
                cancellationToken).ConfigureAwait(false);
        }
    }
}

