// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CommonLanguageServerProtocol.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Roslyn.LanguageServer.Protocol;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// Defines a <see cref="IProtocolSerializer"/> if the underlying <see cref="StreamJsonRpc.JsonRpc"/> is using Newtonsoft for serialization.
/// </summary>
internal class RoslynNewtonsoftProtocolSerializer(JsonSerializer jsonSerializer, ILspLogger logger) : NewtonsoftProtocolSerializer(jsonSerializer)
{
    public override string GetLanguageNameForRequest(object? parameters, string methodName, ILspServices lspServices)
    {
        if (parameters == null)
        {
            logger.LogInformation("No request parameters given, using default language handler");
            return LanguageServerConstants.DefaultLanguageName;
        }

        if (parameters is not JToken request)
        {
            throw new InvalidOperationException($"Request is not a JToken object for {methodName}");
        }

        // For certain requests like text syncing we'll always use the default language handler
        // as we do not want languages to be able to override them.
        if (ShouldUseDefaultLanguage(methodName))
        {
            return LanguageServerConstants.DefaultLanguageName;
        }

        var lspWorkspaceManager = lspServices.GetRequiredService<LspWorkspaceManager>();

        // All general LSP spec document params have the following json structure
        // { "textDocument": { "uri": "<uri>" ... } ... }
        //
        // We can easily identify the URI for the request by looking for this structure
        var textDocumentToken = request["textDocument"];
        if (textDocumentToken is not null)
        {
            var uriToken = textDocumentToken["uri"];
            Contract.ThrowIfNull(uriToken, "textDocument does not have a uri property");
            var uri = uriToken.ToObject<Uri>(JsonSerializer);
            Contract.ThrowIfNull(uri, "Failed to deserialize uri property");
            var language = lspWorkspaceManager.GetLanguageForUri(uri);
            logger.LogInformation($"Using {language} from request text document");
            return language;
        }

        // All the LSP resolve params have the following known json structure
        // { "data": { "TextDocument": { "uri": "<uri>" ... } ... } ... }
        //
        // We can deserialize the data object using our unified DocumentResolveData.
        var dataToken = request["data"];
        if (dataToken is not null)
        {
            var data = dataToken.ToObject<DocumentResolveData>(JsonSerializer);
            Contract.ThrowIfNull(data, "Failed to document resolve data object");
            var language = lspWorkspaceManager.GetLanguageForUri(data.TextDocument.Uri);
            logger.LogInformation($"Using {language} from data text document");
            return language;
        }

        // This request is not for a textDocument and is not a resolve request.
        logger.LogInformation("Request did not contain a textDocument, using default language handler");
        return LanguageServerConstants.DefaultLanguageName;

        static bool ShouldUseDefaultLanguage(string methodName)
        {
            return methodName switch
            {
                Methods.InitializeName => true,
                Methods.InitializedName => true,
                Methods.TextDocumentDidOpenName => true,
                Methods.TextDocumentDidChangeName => true,
                Methods.TextDocumentDidCloseName => true,
                Methods.TextDocumentDidSaveName => true,
                Methods.ShutdownName => true,
                Methods.ExitName => true,
                _ => false,
            };
        }
    }
}
