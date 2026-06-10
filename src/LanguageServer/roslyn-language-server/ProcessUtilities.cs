// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;

namespace Microsoft.CodeAnalysis.LanguageServer.Client;

internal static class ProcessUtilities
{
    public static Task ForwardStandardErrorAsync(Process process, CancellationToken cancellationToken)
    {
        if (process is null)
            throw new ArgumentNullException(nameof(process));

        return Task.Run(async () =>
        {
            try
            {
                var standardError = Console.Error;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                        break;

                    await standardError.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await standardError.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (IOException)
            {
            }
        }, CancellationToken.None);
    }

    public static string GetCommandLineForDisplay(ServerExecutable executable, IReadOnlyList<string> arguments)
    {
        if (executable is null)
            throw new ArgumentNullException(nameof(executable));
        if (arguments is null)
            throw new ArgumentNullException(nameof(arguments));

        var parts = new List<string>();
        parts.Add(executable.FileName);
        if (executable.FirstArgument is not null)
            parts.Add(executable.FirstArgument);
        parts.AddRange(arguments);
        return string.Join(" ", parts.Select(QuoteForDisplay));
    }

    public static async Task WaitForExitOrKillAsync(Process process, TimeSpan timeout)
    {
        if (process is null)
            throw new ArgumentNullException(nameof(process));

        try
        {
            using var cancellationSource = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cancellationSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    private static string QuoteForDisplay(string value)
    {
        if (value.Length == 0)
            return "\"\"";

        if (!value.Any(static c => char.IsWhiteSpace(c) || c == '"'))
            return value;

        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
