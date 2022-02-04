// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host.Mef;
using StreamJsonRpc;

namespace Microsoft.CodeAnalysis.LanguageServer
{
    internal interface ILspLoggerFactory
    {
        Task<ILspLogger> CreateLoggerAsync(string serverTypeName, string? clientName, JsonRpc jsonRpc, CancellationToken cancellationToken);
    }

    [Export(typeof(LspWorkspaceRegistrationService)), Shared]
    internal class DefaultWorkspaceRegistrationService : LspWorkspaceRegistrationService
    {
        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public DefaultWorkspaceRegistrationService()
        {
        }

        public override string GetHostWorkspaceKind() => WorkspaceKind.Host;
    }

    [Export(typeof(ILspLoggerFactory)), Shared]
    internal class NoOpLoggerFactory : ILspLoggerFactory
    {
        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public NoOpLoggerFactory()
        {
        }

        public Task<ILspLogger> CreateLoggerAsync(string serverTypeName, string? clientName, JsonRpc jsonRpc, CancellationToken cancellationToken)
        {
            return Task.FromResult(NoOpLspLogger.Instance);
        }
    }

}
