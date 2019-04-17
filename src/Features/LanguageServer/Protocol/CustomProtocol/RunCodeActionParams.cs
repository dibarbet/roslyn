// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.CustomProtocol
{
    /// <summary>
    /// Params of a RunCodeAction command that is returned by the GetCodeActionsHandler.
    /// </summary>
    public class RunCodeActionParams
    {
        /// <summary>
        /// Params that were passed to originally get a list of codeactions.
        /// </summary>
        public CodeActionParams CodeActionParams { get; set; }

        /// <summary>
        /// Title of the action to execute.
        /// </summary>
        public string Title { get; set; }
    }
}
