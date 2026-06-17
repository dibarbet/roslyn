// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Handler.Diagnostics;
using Microsoft.CodeAnalysis.LanguageServer.Handler.Diagnostics.DiagnosticSources;
using Microsoft.CodeAnalysis.Options;

namespace Microsoft.CodeAnalysis.ExternalAccess.VSTypeScript;

/// <summary>
/// The TypeScript-contract per-server export of <see cref="DocumentPullDiagnosticHandler"/>.
/// </summary>
[ExportLspService(typeof(DocumentPullDiagnosticHandler), ProtocolConstants.TypeScriptLanguageContract), Shared(ProtocolConstants.LspServerInstanceSharingBoundary)]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class VSTypeScriptDocumentPullDiagnosticHandler(
    IDiagnosticSourceManager diagnosticSourceManager,
    IDiagnosticsRefresher diagnosticRefresher,
    IGlobalOptionService globalOptions)
    : DocumentPullDiagnosticHandler(diagnosticSourceManager, diagnosticRefresher, globalOptions);

/// <summary>
/// The TypeScript-contract per-server export of <see cref="WorkspacePullDiagnosticHandler"/>.
/// </summary>
[ExportLspService(typeof(WorkspacePullDiagnosticHandler), ProtocolConstants.TypeScriptLanguageContract), Shared(ProtocolConstants.LspServerInstanceSharingBoundary)]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class VSTypeScriptWorkspacePullDiagnosticHandler(
    IDiagnosticSourceManager diagnosticSourceManager,
    IDiagnosticsRefresher diagnosticsRefresher,
    IGlobalOptionService globalOptions,
    LspServices lspServices)
    : WorkspacePullDiagnosticHandler(
        lspServices.GetRequiredService<LspWorkspaceManager>(),
        lspServices.GetRequiredService<LspWorkspaceRegistrationService>(),
        diagnosticSourceManager,
        diagnosticsRefresher,
        globalOptions);
