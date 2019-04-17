// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.CodeAnalysis.LanguageServer.CustomProtocol
{
    /// <summary>
    /// Response from a <see cref="RoslynMethods.Projects"/> reqeuest
    /// </summary>
    public class Project
    {
        /// <summary>
        /// Name of the project.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The project language.
        /// </summary>
        public string Language { get; set; }

        /// <summary>
        /// Paths of the files in the project.
        /// </summary>
#pragma warning disable CA1819
        public Uri[] SourceFiles { get; set; }
#pragma warning restore
    }
}
