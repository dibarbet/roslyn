// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.CodeAnalysis.LanguageServer;

namespace Microsoft.CodeAnalysis.ExternalAccess.Xaml;

/// <summary>
/// Defines an easy to use subclass for <see cref="ExportLspServiceAttribute"/> with the Roslyn languages contract name.
/// <para>
/// Pair this with <c>[Shared(ProtocolConstants.LspServerInstanceSharingBoundary)]</c> on the service to get a
/// per-server instance. A <see cref="XamlRequestHandlerBase{TRequest, TResponse}"/> exported this way reads
/// resolve-data via <see cref="XamlRequestContext"/>, so it no longer needs a factory or an injected
/// <see cref="IResolveCachedDataService"/>. This replaces the obsolete
/// <see cref="XamlRequestHandlerFactoryBase{TRequest, TResponse}"/> /
/// <see cref="ExportXamlLspServiceFactoryAttribute"/> pattern.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false), System.Composition.MetadataAttribute]
internal sealed class ExportXamlLspServiceAttribute : ExportLspServiceAttribute
{
    public ExportXamlLspServiceAttribute(Type type)
        : base(type, ProtocolConstants.RoslynLspLanguagesContract)
    {
    }
}
