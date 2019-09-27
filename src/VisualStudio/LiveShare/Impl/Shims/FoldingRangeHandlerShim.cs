// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Microsoft.VisualStudio.LiveShare.LanguageServices;

namespace Microsoft.VisualStudio.LanguageServices.LiveShare
{
    internal class FoldingRangeHandlerShim : AbstractLiveShareHandlerShim<FoldingRangeParams, FoldingRange[]>
    {
        public FoldingRangeHandlerShim(IEnumerable<Lazy<IRequestHandler, IRequestHandlerMetadata>> requestHandlers)
            : base(requestHandlers, Methods.TextDocumentFoldingRangeName)
        {
        }
    }

    [ExportLspRequestHandler(LiveShareConstants.RoslynContractName, Methods.TextDocumentFoldingRangeName)]
    [Obsolete("Used for backwards compatibility with old liveshare clients.")]
    internal class RoslynFoldingRangeHandlerShim : DocumentHighlightHandlerShim
    {
        [ImportingConstructor]
        public RoslynFoldingRangeHandlerShim([ImportMany] IEnumerable<Lazy<IRequestHandler, IRequestHandlerMetadata>> requestHandlers) : base(requestHandlers)
        {
        }
    }

    [ExportLspRequestHandler(LiveShareConstants.CSharpContractName, Methods.TextDocumentFoldingRangeName)]
    internal class CSharpFoldingRangeHandlerShim : DocumentHighlightHandlerShim
    {
        [ImportingConstructor]
        public CSharpFoldingRangeHandlerShim([ImportMany] IEnumerable<Lazy<IRequestHandler, IRequestHandlerMetadata>> requestHandlers) : base(requestHandlers)
        {
        }
    }

    [ExportLspRequestHandler(LiveShareConstants.VisualBasicContractName, Methods.TextDocumentFoldingRangeName)]
    internal class VisualBasicFoldingRangeHandlerShim : DocumentHighlightHandlerShim
    {
        [ImportingConstructor]
        public VisualBasicFoldingRangeHandlerShim([ImportMany] IEnumerable<Lazy<IRequestHandler, IRequestHandlerMetadata>> requestHandlers) : base(requestHandlers)
        {
        }
    }

    [ExportLspRequestHandler(LiveShareConstants.TypeScriptContractName, Methods.TextDocumentFoldingRangeName)]
    internal class TypeScriptFoldingRangeHandlerShim : DocumentHighlightHandlerShim
    {
        [ImportingConstructor]
        public TypeScriptFoldingRangeHandlerShim([ImportMany] IEnumerable<Lazy<IRequestHandler, IRequestHandlerMetadata>> requestHandlers) : base(requestHandlers)
        {
        }
    }
}
