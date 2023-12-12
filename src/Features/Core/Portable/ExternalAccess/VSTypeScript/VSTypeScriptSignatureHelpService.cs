// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.SignatureHelp;

namespace Microsoft.CodeAnalysis.ExternalAccess.VSTypeScript;

[ExportLanguageServiceFactory(typeof(SignatureHelpService), InternalLanguageNames.TypeScript), Shared]
internal class VSTypeScriptSignatureHelpServiceFactory : ILanguageServiceFactory
{
    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public VSTypeScriptSignatureHelpServiceFactory()
    {
    }

    public ILanguageService CreateLanguageService(HostLanguageServices languageServices)
        => new VSTypeScriptSignatureHelpService(languageServices.LanguageServices);
}

internal class VSTypeScriptSignatureHelpService : SignatureHelpService
{
    internal VSTypeScriptSignatureHelpService(Host.LanguageServices services)
        : base(services)
    {
    }
}
