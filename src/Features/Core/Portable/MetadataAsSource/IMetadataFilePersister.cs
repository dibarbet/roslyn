// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

internal interface IMetadataAsSourceFilePersister
{
    // maybe store kind in the file info, then have other code that knows how to write or read the file depending on the kind.

    // writing only ever happens in the first call to generate the file.  that does not need to be part of the return type to get the generated file.
    // reading may need to happen outside of the call, so that must be part of the return type (potentially the loader).

    // writing is very different depending on the implementation - decompile may want virtual or file system, pdb is alwaysfile system.

    // TODO - caller of generate file needs to know what kind of file it got back, as only virtual files need the client callback.
    // TODO - instead, could the LSP get virtual file just call the text loader to retrieve the text? - text loader registers itself to the get virtual file lsp request?

    // keep loader, create different loaders based on the kind of file.  pdbloader seems like it could use workspace loader (encoding and checksum check).
    Task WriteMetadataFileAsync(
        string metadataFileUri,
        SourceText contents,
        CancellationToken cancellationToken);
}

internal record struct VirtualMetadataFile : IMetadataAsSourceFilePersister
{
    public string MetadataFileUri { get; }

    public VirtualMetadataFile(string metadataFileUri)
    {
        MetadataFileUri = metadataFileUri;
    }
}

internal record struct FileSystemMetadataFile : IMetadataAsSourceFilePersister
{
    public string MetadataFileUri { get; }

    public FileSystemMetadataFile(string metadataFileUri)
    {
        MetadataFileUri = metadataFileUri;
    }
}

internal enum MetadataFileKind
{
    Virtual,
    FileSystem
}
