// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CommonLanguageServerProtocol.Framework;
using Roslyn.Test.Utilities;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests.OnAutoInsert
{
    public class SerializationTests(ITestOutputHelper testOutputHelper) : AbstractLanguageServerProtocolTests(testOutputHelper)
    {
        protected override TestComposition Composition => base.Composition.AddParts(typeof(ArgumentExceptionHandler));

        [Theory, CombinatorialData]
        public async Task TestArgumentExceptionSerialized(bool mutatingLspWorkspace)
        {
            await using var testLspServer = await CreateTestLspServerAsync(string.Empty, mutatingLspWorkspace);

            try
            {
                await testLspServer.ExecuteRequestAsync<object, string>(ArgumentExceptionHandler.MethodName, new object(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                Assert.IsType<RemoteInvocationException>(ex);
                Assert.IsType<ArgumentException>(ex.InnerException);
                Assert.Equal(ArgumentExceptionHandler.ExceptionMessage, ex.InnerException.Message);
            }
        }

        [ExportCSharpVisualBasicStatelessLspService(typeof(ArgumentExceptionHandler)), PartNotDiscoverable, Shared]
        [LanguageServerEndpoint(MethodName, LanguageServerConstants.DefaultLanguageName)]
        [method: ImportingConstructor]
        [method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        internal sealed class ArgumentExceptionHandler() : ILspServiceRequestHandler<object, string>
        {
            public const string MethodName = nameof(ArgumentExceptionHandler);

            public const string ExceptionMessage = "Range={ Start={ Line=74, Character=22 }, End={ Line=192, Character=0 } }. text.Length=4271. text.Lines.Count=131";

            public bool MutatesSolutionState => false;
            public bool RequiresLSPSolution => true;

            public Task<string> HandleRequestAsync(object request, RequestContext context, CancellationToken cancellationToken)
            {
                throw new ArgumentException(ExceptionMessage);
            }
        }
    }
}
