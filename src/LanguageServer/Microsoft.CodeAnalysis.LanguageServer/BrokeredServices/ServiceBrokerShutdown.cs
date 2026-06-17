// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CommonLanguageServerProtocol.Framework;

namespace Microsoft.CodeAnalysis.LanguageServer.BrokeredServices;

[ExportCSharpVisualBasicLspService(typeof(ServiceBrokerShutdown)), Shared(ProtocolConstants.LspServerInstanceSharingBoundary)]
internal class ServiceBrokerShutdown(ServiceBrokerFactory serviceBrokerFactory) : IOnServerShutdown, ILspService
{
    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public ServiceBrokerShutdown(LspServices lspServices)
        : this(lspServices.GetRequiredService<ServiceBrokerFactory>())
    {
    }

    public Task ExitAsync()
    {
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync()
    {
        await serviceBrokerFactory.ShutdownAndWaitForCompletionAsync().ConfigureAwait(false);
    }
}
