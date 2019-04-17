// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.CodeAnalysis.LanguageServer.CustomProtocol
{
    public class RoslynTaggedText
    {
        public string Tag { get; set; }
        public string Text { get; set; }

        public TaggedText ToTaggedText() => new TaggedText(Tag, Text);
    }
}
