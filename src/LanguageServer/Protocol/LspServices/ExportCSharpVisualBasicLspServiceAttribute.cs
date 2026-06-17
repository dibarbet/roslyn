// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler;

/// <summary>
/// Defines an easy to use subclass for <see cref="ExportLspServiceAttribute"/> with the roslyn languages contract name.
/// <para>
/// Pair this with <c>[Shared(ProtocolConstants.LspServerInstanceSharingBoundary)]</c> for a per-server
/// service (the replacement for <see cref="ExportCSharpVisualBasicLspServiceFactoryAttribute"/>).
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false), MetadataAttribute]
internal class ExportCSharpVisualBasicLspServiceAttribute(Type type, WellKnownLspServerKinds serverKind = WellKnownLspServerKinds.Any)
    : ExportLspServiceAttribute(type, ProtocolConstants.RoslynLspLanguagesContract, serverKind);
