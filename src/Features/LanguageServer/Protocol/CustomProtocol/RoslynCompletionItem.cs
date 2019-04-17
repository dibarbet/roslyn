// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Runtime.Serialization;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Microsoft.VisualStudio.Text.Adornments;
using Newtonsoft.Json;

namespace Microsoft.CodeAnalysis.LanguageServer.CustomProtocol
{
    /// <summary>
    /// A completion item to merge <see cref="VSCompletionItem"/> and custom tags for roslyn completion item.
    /// </summary>
    [DataContract]
    public class RoslynCompletionItem : CompletionItem
    {
        /// <summary>
        /// A set of custom tags on a completion item. Roslyn has information here to get icons.
        /// </summary>
        [DataMember(Name = "tags")]
        public string[] Tags { get; set; }

        /// <summary>
        /// The description for a completion item.
        /// </summary>
#pragma warning disable CA1819
        [DataMember(Name = "description")]
        public RoslynTaggedText[] Description { get; set; }
#pragma warning restore

        /// <summary>
        /// Gets or sets the icon to show for the completion item. In VS, this is more extensive than the completion kind.
        /// </summary>
        [DataMember(Name = "icon")]
        [JsonConverter(typeof(ObjectContentConverter))]
        public ImageElement Icon { get; set; }

        /// <summary>
        /// Gets or sets the description for a completion item.
        /// </summary>
        [DataMember(Name = "description")]
        [JsonConverter(typeof(ObjectContentConverter))]
        public ClassifiedTextElement Description { get; set; }

        public static RoslynCompletionItem From(CompletionItem completionItem)
        {
            return new RoslynCompletionItem
            {
                AdditionalTextEdits = completionItem.AdditionalTextEdits,
                Command = completionItem.Command,
                CommitCharacters = completionItem.CommitCharacters,
                Data = completionItem.Data,
                Detail = completionItem.Detail,
                Documentation = completionItem.Documentation,
                FilterText = completionItem.FilterText,
                InsertText = completionItem.InsertText,
                InsertTextFormat = completionItem.InsertTextFormat,
                Kind = completionItem.Kind,
                Label = completionItem.Label,
                SortText = completionItem.SortText,
                TextEdit = completionItem.TextEdit
            };
        }
    }
}
