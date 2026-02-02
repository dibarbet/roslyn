// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Roslyn.LanguageServer.Protocol;

internal sealed partial class DocumentUri
{
    public static bool operator ==(DocumentUri? left, DocumentUri? right)
        => left is null ? right is null : left.Equals(right);

    public static bool operator !=(DocumentUri? left, DocumentUri? right)
        => !(left == right);
}
