// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CommonLanguageServerProtocol.Framework;
using Roslyn.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.HostWorkspace;

[ExportCSharpVisualBasicLspService(typeof(OpenSolutionHandler)), Shared(ProtocolConstants.LspServerInstanceSharingBoundary)]
[Method(OpenSolutionName)]
internal sealed class OpenSolutionHandler : ILspService, ILspServiceNotificationHandler<OpenSolutionHandler.NotificationParams>
{
    internal const string OpenSolutionName = "solution/open";

    private readonly LanguageServerProjectSystem _projectSystem;

    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public OpenSolutionHandler(LspServices lspServices)
        : this(lspServices.GetRequiredService<LanguageServerProjectSystem>())
    {
    }

    public OpenSolutionHandler(LanguageServerProjectSystem projectSystem)
    {
        _projectSystem = projectSystem;
    }

    public bool MutatesSolutionState => false;
    public bool RequiresLSPSolution => false;

    Task INotificationHandler<NotificationParams, RequestContext>.HandleNotificationAsync(NotificationParams request, RequestContext requestContext, CancellationToken cancellationToken)
    {
        return _projectSystem.OpenSolutionAsync(request.Solution.GetDocumentFilePathFromUri());
    }

    internal sealed class NotificationParams
    {
        [JsonPropertyName("solution")]
        public required DocumentUri Solution { get; set; }
    }
}
