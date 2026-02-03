// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.CodeAnalysis;

namespace Roslyn.LanguageServer.Protocol;

/// <summary>
/// Datatype used to hold URI strings for LSP message serialization.  For details on how URIs are communicated in LSP,
/// see https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#uri
/// </summary>
/// <remarks>
/// While .NET has a type represent URIs (System.Uri), we do not want to use this type directly in serialization and
/// deserialization.  System.Uri does full parsing and validation on the URI upfront, so any issues in the uri format
/// will cause deserialization to fail and bypass any of our error recovery.
/// 
/// Compounding this problem, System.Uri will fail to parse various RFC spec valid URIs. In order to gracefully handle
/// these issues, we defer the parsing of the URI until someone actually asks for it (and can handle the failure).
/// </remarks>
internal sealed partial class DocumentUri(string uriString)
{
    public string UriString { get; } = uriString;
    private Optional<Uri> _parsedUri;

    public DocumentUri(Uri parsedUri) : this(parsedUri.AbsoluteUri)
        => _parsedUri = parsedUri;

    /// <summary>
    /// Gets the parsed System.Uri for the URI string.
    /// </summary>
    /// <returns>
    /// Null if the URI string is not parse-able with System.Uri.
    /// </returns>
    /// <remarks>
    /// Invalid RFC spec URI strings are not parse-able as so will return null here. However, System.Uri can also fail
    /// to parse certain valid RFC spec URI strings.
    /// 
    /// For example, any URI containing a 'sub-delims' character in the host name is a valid RFC spec URI, but will fail
    /// with System.Uri
    /// </remarks>
    public Uri? ParsedUri
    {
        get
        {
            _parsedUri = _parsedUri.HasValue ? _parsedUri : ParseUri(UriString);
            return _parsedUri.Value;
        }
    }

    private static Uri? ParseUri(string uriString)
    {
        try
        {
            return new Uri(uriString);
        }
        catch (UriFormatException)
        {
            // This is not a URI that System.Uri can handle.
            return null;
        }
    }

    public override string ToString() => UriString;

    public override bool Equals(object? obj)
        => obj is DocumentUri other && Equals(other);

    public bool Equals(DocumentUri otherUri)
    {
        if (otherUri is null)
            return false;

        // 99% of the time the equivalent URIs will have equivalent URI strings, as the client is expected to be
        // consistent in how it sends the URIs to the server, either always encoded or always unencoded. See
        // https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#uri
        if (this.UriString == otherUri.UriString)
            return true;

        // Bail if we cannot parse either of the URIs.  We already determined the URI strings are not equal and we need
        // to be able to parse the URIs to do deeper equivalency checks.
        if (otherUri.ParsedUri is null || this.ParsedUri is null)
            return false;

        return Equals(this.ParsedUri.ToString(), otherUri.ParsedUri.ToString());
    }

    public override int GetHashCode()
    {
        // We can't do anything better than the uri string hash code if we cannot parse the URI.
        if (this.ParsedUri is null)
            return this.UriString.GetHashCode();

        // Since the Uri type does not consider an encoded Uri equal to an unencoded Uri, we need to handle this
        // ourselves. The AbsoluteUri property is always encoded, so we can use this to compare the URIs (see Equals
        // above).
        //
        // However, depending on the kind of URI, case sensitivity in AbsoluteUri should be ignored. Uri.GetHashCode
        // normally handles this internally, but the parameters it uses to determine which comparison to use are not
        // exposed.
        //
        // Instead, we will always create the hash code ignoring case, and will rely on the Equals implementation to
        // handle collisions (between two Uris with different casing).  This should be very rare in practice. Collisions
        // can happen for non UNC URIs (e.g. `git:/blah` vs `git:/Blah`).
        return this.ParsedUri.IsAbsoluteUri
            ? StringComparer.OrdinalIgnoreCase.GetHashCode(this.ParsedUri.AbsoluteUri)
            : this.ParsedUri.GetHashCode();
    }
}
