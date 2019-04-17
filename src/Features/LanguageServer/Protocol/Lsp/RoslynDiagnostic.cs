// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Runtime.Serialization;

// TODO - Remove once we have a new version of this package.
namespace Microsoft.VisualStudio.LanguageServer.Protocol
{
    [DataContract]
    public class RoslynDiagnostic : Diagnostic
    {
        /// <summary>
        /// Custom tags on diagnostics - used by analyzers for things like marking a location as unnecessary.
        /// </summary>
        [DataMember(Name = "tags")]
        public string[] Tags { get; set; }
    }
}
