// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO.Pipes;

namespace Microsoft.CodeAnalysis.LanguageServer.Daemon;

/// <summary>
/// Helpers to create the daemon's named-pipe server/client streams. Uses
/// <see cref="PipeOptions.CurrentUserOnly"/> for user-scoping on all platforms (.NET Core),
/// matching the connection the C# extension and the compiler server already use.
/// </summary>
internal static class DaemonNamedPipe
{
    private const int PipeBufferSize = 0x10000;

    /// <summary>
    /// Creates a server-side named pipe stream that allows multiple concurrent instances, so the
    /// daemon can accept many clients on the same pipe name. Each accepted connection gets its own
    /// dedicated, fully independent stream.
    /// </summary>
    public static NamedPipeServerStream CreateServerStream(string pipeName)
        => new(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            PipeBufferSize,
            PipeBufferSize);

    /// <summary>
    /// Creates a client-side named pipe stream connecting to the local daemon.
    /// </summary>
    public static NamedPipeClientStream CreateClientStream(string pipeName)
        => new(
            serverName: ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}
