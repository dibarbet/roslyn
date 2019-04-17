// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

// TODO - Remove once we have a new version of this package.
namespace Microsoft.VisualStudio.LanguageServer.Protocol
{
    /// <summary>
    /// Enum which represents the various kinds of code actions.
    /// </summary>
    [DataContract]
    [JsonConverter(typeof(StringEnumConverter))]
    public enum CodeActionKind
    {
        /// <summary>
        /// Code action is a quick fix.
        /// </summary>
        [EnumMember(Value = "quickfix")]
        QuickFix,

        /// <summary>
        /// Code action is a refactor
        /// </summary>
        [EnumMember(Value = "refactor")]
        Refactor,

        /// <summary>
        /// Code action is a refactor for extracting methods, functions, variables, etc.
        /// </summary>
        [EnumMember(Value = "refactor.extract")]
        RefactorExtract,

        /// <summary>
        /// Code action is a refactor for inlining methods, constants, etc.
        /// </summary>
        [EnumMember(Value = "refactor.inline")]
        RefactorInline,

        /// <summary>
        /// Code action is a refactor for rewrite actions, such as making methods static.
        /// </summary>
        [EnumMember(Value = "refactor.rewrite")]
        RefactorRewrite,

        /// <summary>
        /// Code action applies to the entire file.
        /// </summary>
        [EnumMember(Value = "source")]
        Source,

        /// <summary>
        /// Code actions is for organizing imports.
        /// </summary>
        [EnumMember(Value = "source.organizeImports")]
        SourceOrganizeImports,
    }
}
