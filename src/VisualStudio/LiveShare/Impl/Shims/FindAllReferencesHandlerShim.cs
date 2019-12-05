// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Microsoft.VisualStudio.LiveShare.LanguageServices;

namespace Microsoft.VisualStudio.LanguageServices.LiveShare
{
    [ExportLspRequestHandler(LiveShareConstants.RoslynContractName, Methods.TextDocumentReferencesName)]
    [Obsolete("Used for backwards compatibility with old liveshare clients.")]
    internal class RoslynFindAllReferencesHandler : AbstractLiveShareHandlerOnMainThreadShim<ReferenceParams, object[]>
    {
        [ImportingConstructor]
        public RoslynFindAllReferencesHandler(IEnumerable<Lazy<IRequestHandler, IRequestHandlerMetadata>> requestHandlers, IThreadingContext threadingContext)
            : base(requestHandlers, Methods.TextDocumentReferencesName, threadingContext)
        {
        }
    }

    [ExportLspRequestHandler(LiveShareConstants.CSharpContractName, Methods.TextDocumentReferencesName)]
    internal class CSharpFindAllReferencesHandler : AbstractLiveShareHandlerOnMainThreadShim<ReferenceParams, object[]>
    {
        [ImportingConstructor]
        public CSharpFindAllReferencesHandler(IEnumerable<Lazy<IRequestHandler, IRequestHandlerMetadata>> requestHandlers, IThreadingContext threadingContext)
            : base(requestHandlers, Methods.TextDocumentReferencesName, threadingContext)
        {
        }
    }

    [ExportLspRequestHandler(LiveShareConstants.VisualBasicContractName, Methods.TextDocumentReferencesName)]
    internal class VisualBasicFindAllReferencesHandler : AbstractLiveShareHandlerOnMainThreadShim<ReferenceParams, object[]>
    {
        [ImportingConstructor]
        public VisualBasicFindAllReferencesHandler(IEnumerable<Lazy<IRequestHandler, IRequestHandlerMetadata>> requestHandlers, IThreadingContext threadingContext)
            : base(requestHandlers, Methods.TextDocumentReferencesName, threadingContext)
        {
        }
    }

    [ExportLspRequestHandler(LiveShareConstants.TypeScriptContractName, Methods.TextDocumentReferencesName)]
    internal class TypeScriptFindAllReferencesHandler : AbstractLiveShareHandlerOnMainThreadShim<ReferenceParams, object[]>
    {
        [ImportingConstructor]
        public TypeScriptFindAllReferencesHandler(IEnumerable<Lazy<IRequestHandler, IRequestHandlerMetadata>> requestHandlers, IThreadingContext threadingContext)
            : base(requestHandlers, Methods.TextDocumentReferencesName, threadingContext)
        {
        }
    }
}
