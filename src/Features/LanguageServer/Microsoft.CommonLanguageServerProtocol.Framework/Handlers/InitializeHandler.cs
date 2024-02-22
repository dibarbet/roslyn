// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.CommonLanguageServerProtocol.Framework.Handlers;

#if CLASP_SOURCE_PACKAGE
[System.CodeDom.Compiler.GeneratedCode("Microsoft.CommonLanguageServerProtocol.Framework", "1.0")]
#endif
[LanguageServerEndpoint("initialize", LanguageServerConstants.DefaultLanguageName)]
#if BINARY_COMPAT // TODO - Remove with https://github.com/dotnet/roslyn/issues/72251
public class InitializeHandler<TRequest, TResponse, TRequestContext>
#else
internal class InitializeHandler<TRequest, TResponse, TRequestContext>
#endif
    : IRequestHandler<TRequest, TResponse, TRequestContext>
{
    private readonly IInitializeManager<TRequest, TResponse> _capabilitiesManager;

    public InitializeHandler(IInitializeManager<TRequest, TResponse> capabilitiesManager)
    {
        _capabilitiesManager = capabilitiesManager;
    }

    public bool MutatesSolutionState => true;

    public Task<TResponse> HandleRequestAsync(TRequest request, TRequestContext context, CancellationToken cancellationToken)
    {
        _capabilitiesManager.SetInitializeParams(request);

        var serverCapabilities = _capabilitiesManager.GetInitializeResult();

        return Task.FromResult(serverCapabilities);
    }
}
