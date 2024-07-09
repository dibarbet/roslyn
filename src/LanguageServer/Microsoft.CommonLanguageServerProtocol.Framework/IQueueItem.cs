// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

// This is consumed as 'generated' code in a source package and therefore requires an explicit nullable enable
#nullable enable

namespace Microsoft.CommonLanguageServerProtocol.Framework;

/// <summary>
/// An item to be queued for execution.
/// </summary>
/// <typeparam name="TRequestContext">The type of the request context to be passed along to the handler.</typeparam>
internal interface IQueueItem<TRequestContext>
{
    /// <summary>
    /// Executes the work specified by this queue item.
    /// </summary>
    /// <param name="requestContext">the context created by <see cref="ResolveQueueItemAsync(AbstractLanguageServer{TRequestContext}, CancellationToken)"/></param>
    /// <param name="handlerDelegate">The delegate used to invoke the request.</param>
    /// <param name="cancellationToken" />
    /// <returns>A <see cref="Task "/> which completes when the request has finished.</returns>
    public Task StartRequestAsync<TResponse>(string language, TRequestContext? context, Delegate handlerDelegate, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the language, handler, and request context needed to process this item, but does not run the request.
    /// This is called serially by <see cref="RequestExecutionQueue{TRequestContext}.ProcessQueueAsync"/>
    /// Throwing in this method will cause the server to shutdown.
    /// </summary>
    public Task<(TRequestContext Context, string Language, IMethodHandler handler, Delegate handlerEntryPoint)> ResolveQueueItemAsync(AbstractLanguageServer<TRequestContext> server, CancellationToken cancellationToken);

    /// <summary>
    /// Provides access to LSP services.
    /// </summary>
    ILspServices LspServices { get; }

    /// <summary>
    /// The method being executed.
    /// </summary>
    string MethodName { get; }

    IMethodHandler DefaultHandler { get; }
}
