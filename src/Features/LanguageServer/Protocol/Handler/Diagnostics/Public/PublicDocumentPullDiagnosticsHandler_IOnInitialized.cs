﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.SolutionCrawler;
using Roslyn.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler.Diagnostics.Public;

using DocumentDiagnosticReport = SumType<RelatedFullDocumentDiagnosticReport, RelatedUnchangedDocumentDiagnosticReport>;

// A document diagnostic partial report is defined as having the first literal send = DocumentDiagnosticReport (aka changed / unchanged) followed
// by n DocumentDiagnosticPartialResult literals.
// See https://github.com/microsoft/vscode-languageserver-node/blob/main/protocol/src/common/proposed.diagnostics.md#textDocument_diagnostic
using DocumentDiagnosticPartialReport = SumType<RelatedFullDocumentDiagnosticReport, RelatedUnchangedDocumentDiagnosticReport, DocumentDiagnosticReportPartialResult>;

internal sealed partial class PublicDocumentPullDiagnosticsHandler : IOnInitialized// : AbstractDocumentPullDiagnosticHandler<DocumentDiagnosticParams, DocumentDiagnosticPartialReport, DocumentDiagnosticReport?>, IOnInitialized
{
    public async Task OnInitializedAsync(ClientCapabilities clientCapabilities, RequestContext context, CancellationToken cancellationToken)
    {
        // Dynamically register for all of our document diagnostic sources.
        if (clientCapabilities?.TextDocument?.Diagnostic?.DynamicRegistration is true)
        {
            // TODO: Hookup an option changed handler for changes to BackgroundAnalysisScopeOption
            //       to dynamically register/unregister the non-local document diagnostic source.

            var sources = DiagnosticSourceManager.GetSourceNames(isDocument: true).Where(source => source != PullDiagnosticCategories.Task);
            await _clientLanguageServerManager.SendRequestAsync(
                methodName: Methods.ClientRegisterCapabilityName,
                @params: new RegistrationParams()
                {
                    Registrations = sources.Select(FromSourceName).ToArray()
                },
                cancellationToken).ConfigureAwait(false);
        }

        Registration FromSourceName(string sourceName)
        {
            return new()
            {
                Id = Guid.NewGuid().ToString(),
                Method = Methods.TextDocumentDiagnosticName,
                RegisterOptions = new DiagnosticRegistrationOptions { Identifier = sourceName, InterFileDependencies = true }
            };
        }
    }
}
