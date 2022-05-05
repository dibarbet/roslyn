// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.VisualStudio.Composition;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer
{
    [Export(typeof(ILspServiceProvider)), Shared]
    internal class LspServiceProvider : ILspServiceProvider
    {
        private readonly ExportProvider _exportProvider;

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public LspServiceProvider(ExportProvider exportProvider)
        {
            _exportProvider = exportProvider;
        }

        public LspServices CreateServices(WellKnownLspServerKinds serverKind, ImmutableArray<Lazy<ILspService, LspServiceMetadataView>> baseServices)
        {
            return new LspServices(_exportProvider.AsExportProvider(), serverKind, baseServices);
        }
    }
}
