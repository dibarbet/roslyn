// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Text;
using Microsoft.CodeAnalysis.Editor;
using Microsoft.CodeAnalysis.Editor.Implementation.LanguageClient;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Microsoft.VisualStudio.Utilities;

namespace Microsoft.CodeAnalysis.LanguageServer;

[ContentType("RoslynEditorConfig")]
[Export(typeof(ILanguageClient))]
internal class EditorConfigInProcLanguageClient : AbstractInProcLanguageClient
{
    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public EditorConfigInProcLanguageClient(
        EditorConfigRequestDispatcherFactory editorConfigDispatcherFactory,
        IGlobalOptionService globalOptions,
        IAsynchronousOperationListenerProvider listenerProvider,
        LspWorkspaceRegistrationService lspWorkspaceRegistrationService,
        ILspLoggerFactory lspLoggerFactory,
        IThreadingContext threadingContext) : base(editorConfigDispatcherFactory, globalOptions, listenerProvider, lspWorkspaceRegistrationService, lspLoggerFactory, threadingContext)
    {
    }

    public override WellKnownLspServerKinds ServerKind => WellKnownLspServerKinds.EditorConfigLspServer;

    public override bool ShowNotificationOnInitializeFailed => true;

    protected override ImmutableArray<string> SupportedLanguages => ImmutableArray.Create("EditorConfig");

    public override ServerCapabilities GetCapabilities(ClientCapabilities clientCapabilities)
    {
        return new ServerCapabilities
        {
            TextDocumentSync = new TextDocumentSyncOptions
            {
                Change = TextDocumentSyncKind.Incremental,
                OpenClose = true,
            },
            HoverProvider = true,
        };
    }
}
