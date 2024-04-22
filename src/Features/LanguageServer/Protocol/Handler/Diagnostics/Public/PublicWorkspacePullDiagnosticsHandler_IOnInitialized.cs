// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Roslyn.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler.Diagnostics.Public
{
    internal sealed partial class PublicWorkspacePullDiagnosticsHandler : IOnInitialized
    {
        public async Task OnInitializedAsync(ClientCapabilities clientCapabilities, RequestContext context, CancellationToken cancellationToken)
        {
            var sources = _diagnosticSourceManager.GetSourceNames(isDocument: false).Where(source => source != PullDiagnosticCategories.Task);
            var regParams = new RegistrationParams
            {
                Registrations = sources.Select(FromSourceName).ToArray()
            };
            await _clientLanguageServerManager.SendRequestAsync(
                methodName: Methods.ClientRegisterCapabilityName,
                @params: regParams,
                cancellationToken).ConfigureAwait(false);

            Registration FromSourceName(string sourceName)
            {
                return new()
                {
                    Id = sourceName,
                    // We have to register for document diagnostics in order to get workspace diagnostics
                    // See https://github.com/microsoft/language-server-protocol/issues/1723
                    // Unfortunately this also registers document diagnostics for the identifier.
                    Method = Methods.TextDocumentDiagnosticName,
                    RegisterOptions = new DiagnosticRegistrationOptions { Identifier = sourceName, InterFileDependencies = true, WorkDoneProgress = true, WorkspaceDiagnostics = true }
                };
            }
        }
    }
}
