// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using Newtonsoft.Json;
using System.Runtime.Serialization;

// TODO - Remove once we have a new version of this package.
namespace Microsoft.VisualStudio.LanguageServer.Protocol
{
    /// <summary>
    /// A class representing a change that can be performed in code. A CodeAction must either set
    /// <see cref="CodeAction.Edit"/> or <see cref="CodeAction.Command"/>. If both are supplied,
    /// the edit will be applied first, then the command will be executed.
    /// </summary>
    [DataContract]
    public class CodeAction
    {
        /// <summary>
        /// Gets or sets the human readable title for this code action.
        /// </summary>
        [DataMember(Name = "title")]
        public string Title
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the kind of code action this instance represents.
        /// </summary>
        [DataMember(Name = "kind")]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public CodeActionKind? Kind
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the diagnostics that this code action resolves.
        /// </summary>
        [DataMember(Name = "diagnostics")]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public Diagnostic[] Diagnostics
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the workspace edit that this code action performs.
        /// </summary>
        [DataMember(Name = "edit")]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public WorkspaceEdit Edit
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the command that this code action executes.
        /// </summary>
        [DataMember(Name = "command")]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public Command Command
        {
            get;
            set;
        }
    }
}
