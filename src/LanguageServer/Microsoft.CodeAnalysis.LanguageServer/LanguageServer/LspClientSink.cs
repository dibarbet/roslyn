// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.LanguageServer.Handler;

namespace Microsoft.CodeAnalysis.LanguageServer.LanguageServer;

/// <summary>
/// Wraps an <see cref="IClientLanguageServerManager"/> to provide an <see cref="ILspClientSink"/>
/// that workspace-scoped components can use without directly referencing LSP service types.
/// </summary>
internal sealed class LspClientSink(IClientLanguageServerManager clientManager) : ILspClientSink
{
    public ValueTask SendNotificationAsync(string methodName, CancellationToken cancellationToken)
        => clientManager.SendNotificationAsync(methodName, cancellationToken);

    public ValueTask SendNotificationAsync<TParams>(string methodName, TParams @params, CancellationToken cancellationToken)
        => clientManager.SendNotificationAsync(methodName, @params, cancellationToken);

    public Task<TResponse> SendRequestAsync<TParams, TResponse>(string methodName, TParams @params, CancellationToken cancellationToken)
        => clientManager.SendRequestAsync<TParams, TResponse>(methodName, @params, cancellationToken);

    public ValueTask SendRequestAsync<TParams>(string methodName, TParams @params, CancellationToken cancellationToken)
        => clientManager.SendRequestAsync(methodName, @params, cancellationToken);
}
