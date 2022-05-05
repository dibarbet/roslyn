// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler
{
    /// <summary>
    /// Defines an easy to use subclass for <see cref="ExportLspServiceAttribute"/> with the roslyn languages contract name.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false), MetadataAttribute]
    internal class ExportRoslynLspServiceAttribute : ExportLspServiceAttribute
    {
        public ExportRoslynLspServiceAttribute(Type type, WellKnownLspServerKinds serverKind = WellKnownLspServerKinds.NotSpecified) : base(type, ProtocolConstants.RoslynLspLanguagesContract, serverKind)
        {
        }
    }

    /// <summary>
    /// Defines an easy to use subclass for <see cref="ExportLspServiceFactoryAttribute"/> with the roslyn languages contract name.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false), MetadataAttribute]
    internal class ExportRoslynLspServiceFactoryAttribute : ExportLspServiceFactoryAttribute
    {
        public ExportRoslynLspServiceFactoryAttribute(Type type, WellKnownLspServerKinds serverKind = WellKnownLspServerKinds.NotSpecified) : base(type, ProtocolConstants.RoslynLspLanguagesContract, serverKind)
        {
        }
    }
}
