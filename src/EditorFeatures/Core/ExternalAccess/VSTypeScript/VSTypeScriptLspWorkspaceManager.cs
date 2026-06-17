// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CommonLanguageServerProtocol.Framework;

namespace Microsoft.CodeAnalysis.ExternalAccess.VSTypeScript;

/// <summary>
/// The TypeScript-contract per-server export of <see cref="LspWorkspaceManager"/>.
/// </summary>
[ExportLspService(typeof(LspWorkspaceManager), ProtocolConstants.TypeScriptLanguageContract), Shared(ProtocolConstants.LspServerInstanceSharingBoundary)]
internal sealed class VSTypeScriptLspWorkspaceManager : LspWorkspaceManager
{
    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public VSTypeScriptLspWorkspaceManager(LspServices lspServices)
        : base(
            lspServices.GetRequiredService<ILspLogger>(),
            lspServices.GetService<ILspMiscellaneousFilesWorkspaceProvider>(),
            lspServices.GetRequiredService<LspWorkspaceRegistrationService>(),
            lspServices.GetRequiredService<ILanguageInfoProvider>(),
            lspServices.GetRequiredService<RequestTelemetryLogger>())
    {
    }
}
