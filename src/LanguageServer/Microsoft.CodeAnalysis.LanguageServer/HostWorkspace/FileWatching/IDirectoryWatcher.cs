// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.ProjectSystem;

namespace Microsoft.CodeAnalysis.LanguageServer.HostWorkspace.FileWatching;

internal readonly record struct DirectoryWatchOptions(ImmutableArray<string> Filters, bool IncludeSubdirectories)
{
    public bool HasSameConfiguration(DirectoryWatchOptions other)
        => IncludeSubdirectories == other.IncludeSubdirectories && Filters.SequenceEqual(other.Filters);
}

internal interface IDirectoryWatcher : IDisposable
{
    void Update(DirectoryWatchOptions options);
}

/// <summary>
/// Supplies the backend for the shared directory tree. An update scope groups changes to coverage;
/// asynchronous backends must establish replacement coverage before retiring previous watches.
/// </summary>
internal interface IDirectoryWatcherFactory
{
    StringComparison PathComparison { get; }

    // Explicit flat LSP watches must not become recursive watches subject to client exclusions.
    bool SeparateFlatWatches { get; }

    bool CanWatchDirectory(string path);
    bool CanConsolidate(string path);
    int GetWatchCount(DirectoryWatchOptions options);

    IDisposable BeginUpdate();
    IDirectoryWatcher Create(string path, DirectoryWatchOptions options, Action<FileChangedEventArgs> onChanged);
}
