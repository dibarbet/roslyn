// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler
{
    /// <summary>
    /// Default request handler interface so a top level type can be exported.
    /// </summary>
    interface IRequestHandler
    {
    }

    /// <summary>
    /// Default type for a VS affinitized request handler so it can be exported separately.
    /// </summary>
    interface IVisualStudioRequestHandler
    {
    }

    interface IRequestHandler<RequestType, ResponseType> : IRequestHandler
    {
        Task<ResponseType> HandleRequestAsync(Solution solution, RequestType request, ClientCapabilities clientCapabilities, CancellationToken cancellationToken);
    }
}
