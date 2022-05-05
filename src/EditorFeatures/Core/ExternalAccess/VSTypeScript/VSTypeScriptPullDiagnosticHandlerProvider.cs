// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.EditAndContinue;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.LanguageServer.Handler.Diagnostics;
using Microsoft.CodeAnalysis.Options;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.ExternalAccess.VSTypeScript
{
    [ExportLspService(typeof(DocumentPullDiagnosticHandler), ProtocolConstants.TypeScriptLanguageContract)]
    [Method(VSInternalMethods.DocumentPullDiagnosticName)]
    internal class VSTypeScriptDocumentPullDiagnosticHandler : DocumentPullDiagnosticHandler
    {
        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public VSTypeScriptDocumentPullDiagnosticHandler(
            IDiagnosticService diagnosticService,
            IDiagnosticAnalyzerService analyzerService,
            EditAndContinueDiagnosticUpdateSource editAndContinueDiagnosticUpdateSource,
            IGlobalOptionService globalOptions) : base(diagnosticService, analyzerService, editAndContinueDiagnosticUpdateSource, globalOptions)
        {
        }
    }

    [ExportLspService(typeof(WorkspacePullDiagnosticHandler), ProtocolConstants.TypeScriptLanguageContract)]
    [Method(VSInternalMethods.WorkspacePullDiagnosticName)]
    internal class VSTypeScriptWorkspacePullDiagnosticHandler : WorkspacePullDiagnosticHandler
    {
        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public VSTypeScriptWorkspacePullDiagnosticHandler(
            IDiagnosticService diagnosticService,
            EditAndContinueDiagnosticUpdateSource editAndContinueDiagnosticUpdateSource,
            IGlobalOptionService globalOptions) : base(diagnosticService, editAndContinueDiagnosticUpdateSource, globalOptions)
        {
        }
    }
}
