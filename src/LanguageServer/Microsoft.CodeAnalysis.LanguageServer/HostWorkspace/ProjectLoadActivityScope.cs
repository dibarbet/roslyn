// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Roslyn.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.HostWorkspace;

/// <summary>
/// Provides <see cref="Activity"/> helpers for project-loading operations.
/// All activities are created as detached trace roots so they appear as independent
/// traces in the trace viewer, not nested under ambient LSP request activities.
/// </summary>
internal static class ProjectLoadActivityScope
{
    private static readonly ActivitySource s_activitySource = new(OpenTelemetryConstants.LanguageServer);

    /// <summary>
    /// Starts a new detached activity (its own trace root).
    /// </summary>
    public static Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal)
    {
        return s_activitySource.StartDetachedActivity(name, kind);
    }

    /// <summary>
    /// Starts a child activity of the given parent activity.
    /// If <paramref name="parent"/> is null, starts a detached root.
    /// </summary>
    public static Activity? StartChildActivity(string name, Activity? parent, ActivityKind kind = ActivityKind.Internal)
    {
        if (parent is not null)
        {
            return s_activitySource.StartActivity(name, kind, parent.Context);
        }

        return s_activitySource.StartDetachedActivity(name, kind);
    }
}
