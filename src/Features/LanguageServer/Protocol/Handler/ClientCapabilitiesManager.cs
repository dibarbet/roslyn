// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler;

[ExportLspService(typeof(IClientCapabilitiesManager), ProtocolConstants.RoslynLspLanguagesContract)]
[ExportLspService(typeof(IClientCapabilitiesManager), ProtocolConstants.XamlLanguageContract)]
[ExportLspService(typeof(IClientCapabilitiesManager), ProtocolConstants.TypeScriptLanguageContract)]
internal class ClientCapabilitiesManager : IClientCapabilitiesManager
{
    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public ClientCapabilitiesManager()
    {
    }

    private ClientCapabilities? _clientCapabilities;

    public ClientCapabilities GetClientCapabilities()
    {
        if (_clientCapabilities is null)
        {
            throw new InvalidOperationException($"Tried to get required {nameof(ClientCapabilities)} before it was set");
        }

        return _clientCapabilities;
    }

    public void SetClientCapabilities(ClientCapabilities clientCapabilities)
    {
        _clientCapabilities = clientCapabilities;
    }

    public ClientCapabilities? TryGetClientCapabilities()
    {
        return _clientCapabilities;
    }
}
