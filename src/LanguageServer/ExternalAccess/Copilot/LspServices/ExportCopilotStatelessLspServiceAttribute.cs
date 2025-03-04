// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.CodeAnalysis.LanguageServer.Handler;

namespace Microsoft.CodeAnalysis.LanguageServer.ExternalAccess.Copilot;

/// <inheritdoc cref="ExportLspServiceAttribute"/>
internal sealed class ExportCopilotStatelessLspServiceAttribute(Type type) :
    ExportCSharpVisualBasicLspServiceAttribute(type, WellKnownLspServerKinds.Any);
