// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
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
    /// <param name="language">the language for the request.</param>
    /// <param name="context">the context created by <see cref="CreateRequestContextAsync(IMethodHandler, CancellationToken)"/></param>
    /// <param name="handler">The handler to use to execute the request.</param>
    /// <param name="cancellationToken" />
    /// <returns>A <see cref="Task "/> which completes when the request has finished.</returns>
    Task StartRequestAsync(string language, TRequestContext? context, IMethodHandler handler, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the context that is sent to the handler for this queue item.
    /// Note - this method is always called serially inside the queue before
    /// running the actual request in <see cref="StartRequestAsync(TRequestContext, IMethodHandler, CancellationToken)"/>
    /// Throwing in this method will cause the server to shutdown.
    /// </summary>
    Task<TRequestContext> CreateRequestContextAsync(IMethodHandler handler, CancellationToken cancellationToken);

    /// <summary>
    /// Provides access to LSP services.
    /// </summary>
    ILspServices LspServices { get; }

    /// <summary>
    /// The method being executed.
    /// </summary>
    string MethodName { get; }

    public AbstractLanguageServer<TRequestContext>.DelegatingEntryPoint EntryPoint { get; }

    public object DeserializedRequest { get; }

    /// <summary>
    /// The type of the request.
    /// </summary>
    Type? RequestType { get; }

    /// <summary>
    /// The type of the response.
    /// </summary>
    Type? ResponseType { get; }
}
