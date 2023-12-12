// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.SignatureHelp;

namespace Microsoft.CodeAnalysis.ExternalAccess.FSharp.Internal.SignatureHelp;

[ExportLanguageServiceFactory(typeof(SignatureHelpService), LanguageNames.FSharp), Shared]
internal class FSharpSignatureHelpServiceFactory : ILanguageServiceFactory
{
    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public FSharpSignatureHelpServiceFactory()
    {
    }

    public ILanguageService CreateLanguageService(HostLanguageServices languageServices)
        => new FSharpSignatureHelpService(languageServices.LanguageServices);
}

internal class FSharpSignatureHelpService : SignatureHelpService
{
    internal FSharpSignatureHelpService(Host.LanguageServices services)
        : base(services)
    {
    }
}
