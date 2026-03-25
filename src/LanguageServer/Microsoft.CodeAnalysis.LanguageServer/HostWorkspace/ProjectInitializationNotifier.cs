// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Composition;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.LanguageServer;

namespace Microsoft.CodeAnalysis.LanguageServer.HostWorkspace;

[Export(typeof(ProjectInitializationNotifier)), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class ProjectInitializationNotifier()
{
    internal const string ProjectInitializationCompleteName = "workspace/projectInitializationComplete";

    public async ValueTask SendProjectInitializationCompleteNotificationAsync(CancellationToken cancellationToken)
    {
        Contract.ThrowIfNull(LanguageServerHost.Instance, "We don't have an LSP channel yet to send this request through.");
        var languageServerManager = LanguageServerHost.Instance.GetRequiredLspService<IClientLanguageServerManager>();
        await languageServerManager.SendNotificationAsync(ProjectInitializationCompleteName, cancellationToken).ConfigureAwait(false);
    }
}
