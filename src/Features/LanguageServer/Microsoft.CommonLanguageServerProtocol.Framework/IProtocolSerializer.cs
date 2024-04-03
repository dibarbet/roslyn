// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Newtonsoft.Json.Linq;

namespace Microsoft.CommonLanguageServerProtocol.Framework;

/// <summary>
/// Defines an interface to abstract away the deserialization of LSP requests.
/// This is a required LSP service.
/// </summary>
#if BINARY_COMPAT // TODO - Remove with https://github.com/dotnet/roslyn/issues/72251
public interface IProtocolSerializer
#else
internal interface IProtocolSerializer
#endif
{
    string GetLanguageNameForRequest(object? request, string methodName, ILspServices lspServices);

    /// <summary>
    /// Converts from the underlying serialization type that StreamJsonRpc gives us (e.g. JToken if using Newtonsoft JsonRpc message handler) into the concrete type.
    /// </summary>
    TType Deserialize<TType>(object request, string method, string language);

    /// <summary>
    /// Converts a concrete type into the serialization type to pass to StreamJsonRpc (e.g. JToken is using Newtsonft JsonRpc message handler).
    /// </summary>
    object? Serialize(object? request);
}
