using System;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.LanguageServer.CustomProtocol;
using Newtonsoft.Json.Linq;
using LSP = Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler.Implementation
{
    /// <summary>
    /// Handles running a codeaction. This is called when a lightbulb action is invoked.
    /// </summary>
    [Shared]
    [ExportLspMethod(LSP.Methods.WorkspaceExecuteCommandName)]
    internal class RunCodeActionsHandler : CodeActionsHandlerBase<LSP.ExecuteCommandParams, object>
    {
        [ImportingConstructor]
        public RunCodeActionsHandler(ICodeFixService codeFixService, ICodeRefactoringService codeRefactoringService)
            : base(codeFixService, codeRefactoringService)
        {
        }

        public override async Task<object> HandleRequestAsync(Solution solution, LSP.ExecuteCommandParams request,
            LSP.ClientCapabilities clientCapabilities, CancellationToken cancellationToken)
        {
            if (request.Command.StartsWith(RemoteCommandNamePrefix))
            {
                var command = ((JObject)request.Arguments[0]).ToObject<LSP.Command>();
                request.Command = command.CommandIdentifier;
                request.Arguments = command.Arguments;
            }
            if (request.Command == RunCodeActionCommandName)
            {
                var runRequest = ((JToken)request.Arguments.Single()).ToObject<RunCodeActionParams>();

                var codeActions = await GetCodeActionsAsync(solution,
                                                            runRequest.CodeActionParams.TextDocument.Uri,
                                                            runRequest.CodeActionParams.Range,
                                                            cancellationToken).ConfigureAwait(false);

                var actionToRun = codeActions?.FirstOrDefault(a => a.Title == runRequest.Title);

                if (actionToRun != null)
                {
                    await SwitchToMainThreadIfApplicable(cancellationToken).ConfigureAwait(true);
                    foreach (var operation in await actionToRun.GetOperationsAsync(cancellationToken).ConfigureAwait(true))
                    {
                        operation.Apply(solution.Workspace, cancellationToken);
                    }
                }
            }
            else
            {
                throw new ArgumentException(string.Format("Invalid command type {0}", request.Command));
            }

            // Return true even in the case that we didn't run the command. Returning false would prompt the guest to tell the host to
            // enable command executuion, which wouldn't solve their problem.
            return true;
        }

        protected virtual async Task SwitchToMainThreadIfApplicable(CancellationToken cancellationToken)
        {
        }
    }
}
