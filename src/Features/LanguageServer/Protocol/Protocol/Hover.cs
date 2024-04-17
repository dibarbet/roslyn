// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Roslyn.LanguageServer.Protocol
{
    using System;
    using System.Runtime.Serialization;
    using Newtonsoft.Json;

    /// <summary>
    /// Class representing the data returned by a textDocument/hover request.
    ///
    /// See the <see href="https://microsoft.github.io/language-server-protocol/specifications/specification-current/#hover">Language Server Protocol specification</see> for additional information.
    /// </summary>
    [DataContract]
    internal class Hover
    {
        /// <summary>
        /// Gets or sets the content for the hover. Object can either be an array or a single object.
        /// If the object is an array the array can contain objects of type <see cref="MarkedString"/> and <see cref="string"/>.
        /// If the object is not an array it can be of type <see cref="MarkedString"/>, <see cref="string"/>, or <see cref="MarkupContent"/>.
        /// </summary>
        // This is nullable because in VS we allow null when VSInternalHover.RawContent is specified instead of Contents
        [DataMember(Name = "contents")]
        public SumType<string, MarkedString, SumType<string, MarkedString>[], MarkupContent>? Contents
        {
            get;
            set;
        }



        /// <summary>
        /// Gets or sets the range over which the hover applies.
        /// </summary>
        [DataMember(Name = "range")]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public Range? Range
        {
            get;
            set;
        }

        // Hover1, Hover2, add new one each time a sumtype changes
        // Could either be new fields in Hover2, or inherit?

        // Explicit fields for each type
        public string StringContents;
        public MarkedString MarkedStringContents;

        // Method to get each field (could be inside a wrapper type)
        //public T GetContents<T>() { }

        // Wrapped type with conversion operators - implicit or explicit?

        //
    }

    [DataContract]
    internal class LspObject
    {
        // Step 1 - initial state.
        /*[DataMember(Name = "tooltip")]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string ToolTip { get; set; }*/


        // Step 2 - add new temp property for new union type for tooltip, obsolete previous property.
        /*[DataMember(Name = "tooltip")]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public SumType<string, MarkupContent> ToolTip2;

        [Obsolete]
        public string ToolTip
        {
            get
            {
                return ToolTip2.First;
            }
            set
            {
                ToolTip2 = value;
            }
        }*/

        // Step 3 - Change type of old property to new property, obsolete new temp prop.
        /*[Obsolete]
        public SumType<string, MarkupContent> ToolTip2;

        [DataMember(Name = "tooltip")]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public SumType<string, MarkupContent> ToolTip { get; set; }*/

        // Step 4 - Delete temp property (now that everyone has moved).
        [DataMember(Name = "tooltip")]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public SumType<string, MarkupContent> ToolTip { get; set; }
    }

    internal class UseLspObject
    {
        void M()
        {
            var obj = new LspObject
            {
                ToolTip = "test"
            };

            string s = obj.ToolTip;
        }
    }

}
