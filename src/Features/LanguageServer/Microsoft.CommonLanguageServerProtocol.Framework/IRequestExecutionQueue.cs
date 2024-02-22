// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.CommonLanguageServerProtocol.Framework;

/// <summary>
/// Queues requests to be executed in the proper order.
/// </summary>
/// <typeparam name="TRequestContext">The type of the RequestContext to be used by the handler.</typeparam>
#if CLASP_SOURCE_PACKAGE
[System.CodeDom.Compiler.GeneratedCode("Microsoft.CommonLanguageServerProtocol.Framework", "1.0")]
#endif
#if BINARY_COMPAT // TODO - Remove with https://github.com/dotnet/roslyn/issues/72251
public interface IRequestExecutionQueue<TRequestContext> : IAsyncDisposable
#else
internal interface IRequestExecutionQueue<TRequestContext> : IAsyncDisposable
#endif
{
    /// <summary>
    /// Queue a request.
    /// </summary>
    /// <returns>A task that completes when the handler execution is done.</returns>
    Task<TResponse> ExecuteAsync<TRequest, TResponse>(TRequest request, string methodName, ILspServices lspServices, CancellationToken cancellationToken);

    /// <summary>
    /// Start the queue accepting requests once any event handlers have been attached.
    /// </summary>
    void Start();
}
