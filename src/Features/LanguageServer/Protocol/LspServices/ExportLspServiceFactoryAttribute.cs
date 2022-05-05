// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;

namespace Microsoft.CodeAnalysis.LanguageServer
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false), MetadataAttribute]
    internal class ExportLspServiceFactoryAttribute : ExportAttribute
    {
        public Type Type { get; }

        public WellKnownLspServerKinds ServerKind { get; }

        public ExportLspServiceFactoryAttribute(Type type, string contractName, WellKnownLspServerKinds serverKind = WellKnownLspServerKinds.NotSpecified) : base(contractName, typeof(ILspServiceFactory))
        {
            Type = type;
            ServerKind = serverKind;
        }
    }
}
