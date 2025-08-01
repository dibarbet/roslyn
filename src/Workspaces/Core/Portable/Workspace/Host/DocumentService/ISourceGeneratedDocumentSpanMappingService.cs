﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.Host;

internal interface ISourceGeneratedDocumentSpanMappingService : IWorkspaceService
{
    Task<ImmutableArray<MappedTextChange>> GetMappedTextChangesAsync(Document oldDocument, Document newDocument, CancellationToken cancellationToken);

    Task<ImmutableArray<MappedSpanResult>> MapSpansAsync(Document document, ImmutableArray<TextSpan> spans, CancellationToken cancellationToken);
}

internal record struct MappedTextChange(string MappedFilePath, TextChange TextChange);
