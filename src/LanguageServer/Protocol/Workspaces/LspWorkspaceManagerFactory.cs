// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CommonLanguageServerProtocol.Framework;

namespace Microsoft.CodeAnalysis.LanguageServer;

[ExportCSharpVisualBasicLspServiceFactory(typeof(LspWorkspaceManager)), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal class LspWorkspaceManagerFactory() : ILspServiceFactory
{
    public ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind)
    {
        var logger = lspServices.GetRequiredLspService<AbstractLspLogger>();
        var miscFilesWorkspace = lspServices.GetLspServiceFromInterface<ILspMiscellaneousFilesWorkspaceProvider>();
        var lspWorkspaceRegistrationService = lspServices.GetRequiredLspService<LspWorkspaceRegistrationService>();
        var languageInfoProvider = lspServices.GetRequiredLspServiceFromInterface<ILanguageInfoProvider>();
        var telemetryLogger = lspServices.GetRequiredLspService<RequestTelemetryLogger>();
        return new LspWorkspaceManager(logger, miscFilesWorkspace, lspWorkspaceRegistrationService, languageInfoProvider, telemetryLogger);
    }
}
