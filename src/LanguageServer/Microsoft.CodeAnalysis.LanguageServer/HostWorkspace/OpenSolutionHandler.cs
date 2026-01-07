// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Composition;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CommonLanguageServerProtocol.Framework;
using Roslyn.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.HostWorkspace;

[ExportCSharpVisualBasicStatelessLspService(typeof(OpenSolutionHandler)), Shared]
[Method(OpenSolutionName)]
internal sealed class OpenSolutionHandler : ILspServiceNotificationHandler<OpenSolutionHandler.NotificationParams>
{
    internal const string OpenSolutionName = "solution/open";

    private readonly LanguageServerProjectSystem _projectSystem;

    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public OpenSolutionHandler(LanguageServerProjectSystem projectSystem)
    {
        _projectSystem = projectSystem;
    }

    public bool MutatesSolutionState => false;
    public bool RequiresLSPSolution => false;

    Task INotificationHandler<NotificationParams, RequestContext>.HandleNotificationAsync(NotificationParams request, RequestContext requestContext, CancellationToken cancellationToken)
    {
        return _projectSystem.OpenSolutionAsync(request.Solution.LocalPath);
    }

    private sealed class NotificationParams
    {
        [JsonPropertyName("solution")]
        public required Uri Solution { get; set; }
    }
}

[ExportCSharpVisualBasicStatelessLspService(typeof(AutoOpenSolutionOnInitialized)), Shared]
internal sealed class AutoOpenSolutionOnInitialized : IOnInitialized, ILspService
{
    private readonly LanguageServerProjectSystem _projectSystem;

    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public AutoOpenSolutionOnInitialized(LanguageServerProjectSystem projectSystem)
    {
        _projectSystem = projectSystem;
    }

    public async Task OnInitializedAsync(ClientCapabilities clientCapabilities, RequestContext context, CancellationToken cancellationToken)
    {
        var slnPath = Environment.GetEnvironmentVariable("CLAUDE_ROSLYN_SLN");
        if (!string.IsNullOrEmpty(slnPath))
        {
            await _projectSystem.OpenSolutionAsync(slnPath);
        }
    }
}
