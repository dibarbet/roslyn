// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.LanguageServer.CustomProtocol;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler.Implementation
{
    /// <summary>
    /// Handler for a request to classify the document. This is used for semantic colorization and only works for C#\VB.
    /// </summary>
    [Shared]
    [ExportLspMethod(RoslynMethods.ClassificationsName)]
    internal class ClassificationsHandler : IRequestHandler<ClassificationParams, ClassificationSpan[]>
    {
        public async Task<ClassificationSpan[]> HandleRequestAsync(Solution solution, ClassificationParams request,
            ClientCapabilities clientCapabilities, CancellationToken cancellationToken)
        {
            var document = solution.GetDocumentFromURI(request.TextDocument.Uri);
            var classificationService = document?.Project.LanguageServices.GetService<IClassificationService>();

            if (document == null || classificationService == null)
            {
                return Array.Empty<ClassificationSpan>();
            }

            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var textSpan = ProtocolConversions.RangeToTextSpan(request.Range, text);

            var spans = new List<ClassifiedSpan>();
            await classificationService.AddSemanticClassificationsAsync(document, textSpan, spans, cancellationToken).ConfigureAwait(false);

            return spans.Select(c => new ClassificationSpan { Classification = c.ClassificationType, Range = ProtocolConversions.TextSpanToRange(c.TextSpan, text) }).ToArray();
        }
    }
}
