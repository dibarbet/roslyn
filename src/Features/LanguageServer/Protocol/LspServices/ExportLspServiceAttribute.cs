// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.LanguageServer
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false), MetadataAttribute]
    internal class ExportLspServiceAttribute : ExportAttribute
    {
        /// <summary>
        /// The type of the service being exported.  Used during retrieval to find the matching service.
        /// </summary>
        public Type Type { get; }

        /// <summary>
        /// The LSP server for which this service applies to.  If null, this service applies to any server
        /// with the matching contract name.
        /// </summary>
        public WellKnownLspServerKinds? ServerKind { get; }

        /// <summary>
        /// Exports an <see cref="ILspService"/> and specifies the contract and type of the service.
        /// </summary>
        /// <param name="contractName">
        /// The contract name this provider is exported.  Used by <see cref="ILspServiceProvider"/>
        /// when importing services to ensure that it only imports services that match this contract.
        /// This is important to ensure that we only load relevant services (e.g. don't load Xaml services when creating the c# server),
        /// otherwise we will get dll load RPS regressions for loading the passed in type.
        /// </param>
        public ExportLspServiceAttribute(Type type, string contractName, WellKnownLspServerKinds serverKind = WellKnownLspServerKinds.NotSpecified) : base(contractName, typeof(ILspService))
        {
            Contract.ThrowIfFalse(type.GetInterfaces().Contains(typeof(ILspService)), $"{type.Name} does not inherit from {nameof(ILspService)}");
            Type = type;
            ServerKind = serverKind;
        }
    }
}
