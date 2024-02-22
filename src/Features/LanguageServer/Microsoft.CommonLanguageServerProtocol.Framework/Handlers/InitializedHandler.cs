// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.CommonLanguageServerProtocol.Framework.Handlers;

#if CLASP_SOURCE_PACKAGE
[System.CodeDom.Compiler.GeneratedCode("Microsoft.CommonLanguageServerProtocol.Framework", "1.0")]
#endif
[LanguageServerEndpoint("initialized", LanguageServerConstants.DefaultLanguageName)]
#if BINARY_COMPAT // TODO - Remove with https://github.com/dotnet/roslyn/issues/72251
public class InitializedHandler<TRequest, TRequestContext> : INotificationHandler<TRequest, TRequestContext>
#else
internal class InitializedHandler<TRequest, TRequestContext> : INotificationHandler<TRequest, TRequestContext>
#endif
{
    private bool HasBeenInitialized = false;

    public bool MutatesSolutionState => true;

    public Task HandleNotificationAsync(TRequest request, TRequestContext requestContext, CancellationToken cancellationToken)
    {
        if (HasBeenInitialized)
        {
            throw new InvalidOperationException("initialized was called twice");
        }

        HasBeenInitialized = true;

        return Task.CompletedTask;
    }
}
