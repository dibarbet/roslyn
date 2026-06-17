// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using Microsoft.CodeAnalysis.LanguageServer;

namespace Microsoft.CodeAnalysis.ExternalAccess.CompilerDeveloperSdk;

/// <summary>
/// Per-server service-locator facade over the language server's <see cref="LspServices"/>, exposed to
/// CompilerDeveloperSdk services. Import this in a service exported with
/// <see cref="ExportCompilerDeveloperSdkLspServiceAttribute"/> to resolve other per-server services.
/// </summary>
[Export, Shared(ProtocolConstants.LspServerInstanceSharingBoundary)]
internal sealed class CompilerDeveloperSdkLspServices
{
    private readonly LspServices _lspServices;

    // Note: this type is also constructed directly by the obsolete AbstractCompilerDeveloperSdkLspServiceFactory,
    // so the importing constructor intentionally omits the error-level Obsolete guard.
    [ImportingConstructor]
    public CompilerDeveloperSdkLspServices(LspServices lspServices)
    {
        _lspServices = lspServices;
    }

    public T GetRequiredService<T>() where T : notnull
        => _lspServices.GetRequiredService<T>();

    public T? GetService<T>() where T : notnull
        => _lspServices.GetService<T>();
}
