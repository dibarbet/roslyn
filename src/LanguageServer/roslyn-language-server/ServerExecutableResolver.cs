// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;

namespace Microsoft.CodeAnalysis.LanguageServer.Client;

internal sealed class ServerExecutable
{
    public ServerExecutable(string toolIdentifier, string fileName, string? firstArgument, string displayPath)
    {
        if (string.IsNullOrEmpty(toolIdentifier))
            throw new ArgumentException("Expected a non-empty tool identifier.", nameof(toolIdentifier));
        if (string.IsNullOrEmpty(fileName))
            throw new ArgumentException("Expected a non-empty file name.", nameof(fileName));
        if (string.IsNullOrEmpty(displayPath))
            throw new ArgumentException("Expected a non-empty display path.", nameof(displayPath));

        ToolIdentifier = toolIdentifier;
        FileName = fileName;
        FirstArgument = firstArgument;
        DisplayPath = displayPath;
    }

    public string ToolIdentifier { get; }
    public string FileName { get; }
    public string? FirstArgument { get; }
    public string DisplayPath { get; }

    public void AddCommandPrefix(ProcessStartInfo startInfo)
    {
        if (startInfo is null)
            throw new ArgumentNullException(nameof(startInfo));

        startInfo.FileName = FileName;
        if (FirstArgument is not null)
            startInfo.ArgumentList.Add(FirstArgument);
    }
}

internal static class ServerExecutableResolver
{
    private const string ServerBaseName = "Microsoft.CodeAnalysis.LanguageServer";

    public static ServerExecutable Resolve()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var appHostPath = Path.Combine(baseDirectory, OperatingSystem.IsWindows() ? ServerBaseName + ".exe" : ServerBaseName);
        var dllPath = Path.Combine(baseDirectory, ServerBaseName + ".dll");

        if (File.Exists(appHostPath))
            return new ServerExecutable(appHostPath, appHostPath, firstArgument: null, displayPath: appHostPath);

        if (File.Exists(dllPath))
            return new ServerExecutable(dllPath, "dotnet", dllPath, displayPath: dllPath);

        throw new FileNotFoundException(
            $"Could not find bundled language server next to the thin client. Expected '{appHostPath}' or '{dllPath}'.");
    }
}
