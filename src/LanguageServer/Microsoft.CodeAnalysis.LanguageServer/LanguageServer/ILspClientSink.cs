// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CodeAnalysis.LanguageServer.LanguageServer;

/// <summary>
/// A sink that allows workspace-scoped components to send notifications and requests
/// back to an LSP client without holding the client services directly.
/// The LSP server provides this when it registers with the <see cref="SharedWorkspaceManager"/>,
/// and the workspace service holds a reference to it.
/// </summary>
internal interface ILspClientSink
{
    ValueTask SendNotificationAsync(string methodName, CancellationToken cancellationToken);
    ValueTask SendNotificationAsync<TParams>(string methodName, TParams @params, CancellationToken cancellationToken);
    Task<TResponse> SendRequestAsync<TParams, TResponse>(string methodName, TParams @params, CancellationToken cancellationToken);
    ValueTask SendRequestAsync<TParams>(string methodName, TParams @params, CancellationToken cancellationToken);
}
