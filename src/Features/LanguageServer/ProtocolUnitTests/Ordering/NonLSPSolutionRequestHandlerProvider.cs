// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Xunit;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests.RequestOrdering
{
#pragma warning disable RS0023 // Parts exported with MEFv2 must be marked with 'SharedAttribute'
    [ExportRoslynLspService(typeof(NonLSPSolutionRequestHandler)), PartNotDiscoverable]
#pragma warning restore RS0023 // Parts exported with MEFv2 must be marked with 'SharedAttribute'
    [Method(MethodName)]
    internal class NonLSPSolutionRequestHandler : IRequestHandler<TestRequest, TestResponse>
    {
        public const string MethodName = nameof(NonLSPSolutionRequestHandler);

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public NonLSPSolutionRequestHandler()
        {
        }

        public bool MutatesSolutionState => false;
        public bool RequiresLSPSolution => false;

        public TextDocumentIdentifier GetTextDocumentIdentifier(TestRequest request) => null;

        public Task<TestResponse> HandleRequestAsync(TestRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            Assert.Null(context.Solution);

            return Task.FromResult(new TestResponse());
        }
    }
}
