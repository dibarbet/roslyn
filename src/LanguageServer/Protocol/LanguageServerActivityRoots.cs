// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;

namespace Roslyn.LanguageServer.Protocol;

/// <summary>
/// Helpers for starting <see cref="Activity"/> instances that are detached from the
/// ambient <see cref="Activity.Current"/>, so each operation becomes its own trace root
/// in the trace viewer.
/// </summary>
internal static class RoslynActivityExtensions
{
    /// <summary>
    /// Starts a new activity that is a new, independent trace root — not a child of
    /// whatever <see cref="Activity.Current"/> happens to be set.
    /// </summary>
    /// <remarks>
    /// <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> always parents
    /// the new activity under <see cref="Activity.Current"/> even when
    /// <c>parentContext: default</c> is passed. The only reliable way to create a
    /// detached root is to temporarily null out <see cref="Activity.Current"/>.
    /// </remarks>
    internal static Activity? StartDetachedActivity(this ActivitySource source, string name, ActivityKind kind = ActivityKind.Internal)
    {
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            return source.StartActivity(name, kind);
        }
        finally
        {
            Activity.Current = previous;
        }
    }
}
