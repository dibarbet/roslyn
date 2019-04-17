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
    /// Parameters for a <see cref="RoslynMethods.Classifications"/> request
    /// </summary>
    public class ClassificationParams
    {
        /// <summary>
        /// The document for which classification is requested.
        /// </summary>
        public TextDocumentIdentifier TextDocument { get; set; }

        /// <summary>
        /// The range for which classification is requested.
        /// </summary>
        public Range Range { get; set; }
    }

    /// <summary>
    /// Response from a <see cref="RoslynMethods.Classifications"/> request
    /// </summary>
    public class ClassificationSpan
    {
        /// <summary>
        /// The range being classified.
        /// </summary>
        public Range Range { get; set; }

        /// <summary>
        /// The classification of the span.
        /// </summary>
        public string Classification { get; set; }
    }
}
