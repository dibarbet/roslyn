// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.CommonLanguageServerProtocol.Framework;

/// <summary>
/// Defines a <see cref="IProtocolSerializer"/> if the underlying <see cref="StreamJsonRpc.JsonRpc"/>
/// is using Newtonsoft for serialization.
/// </summary>
internal class NewtonsoftProtocolSerializer(JsonSerializer jsonSerializer) : IProtocolSerializer
{
    protected readonly JsonSerializer JsonSerializer = jsonSerializer;

    public virtual string GetLanguageNameForRequest(object? request, string methodName, ILspServices lspServices)
    {
        return LanguageServerConstants.DefaultLanguageName;
    }

    public TType Deserialize<TType>(object request, string method, string language)
    {
        if (request is not JToken token)
        {
            throw new InvalidOperationException($"Request is not a JToken object for {method} and language {language}");
        }

        var requestObject = token.ToObject<TType>(JsonSerializer)
                    ?? throw new InvalidOperationException($"Unable to deserialize {request} into {typeof(TType)} for {method} and language {language}");
        return requestObject;
    }

    public object? Serialize(object? request)
    {
        if (request is null)
        {
            return null;
        }

        return JToken.FromObject(request, JsonSerializer);
    }
}
