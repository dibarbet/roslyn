// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;

namespace Microsoft.CommonLanguageServerProtocol.Framework;

/// <summary>
/// Wraps an <see cref="IHandlerProvider"/>.
/// </summary>
#if CLASP_SOURCE_PACKAGE
[System.CodeDom.Compiler.GeneratedCode("Microsoft.CommonLanguageServerProtocol.Framework", "1.0")]
#endif
internal sealed class WrappedHandlerProvider : AbstractHandlerProvider
{
    private readonly IHandlerProvider _handlerProvider;

    public WrappedHandlerProvider(IHandlerProvider handlerProvider)
    {
        _handlerProvider = handlerProvider;
    }

    public override IMethodHandler GetMethodHandler(string method, Type? requestType, Type? responseType, string language)
        => _handlerProvider.GetMethodHandler(method, requestType, responseType);

    public override ImmutableArray<RequestHandlerMetadata> GetRegisteredMethods()
        => _handlerProvider.GetRegisteredMethods();
}
