// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace Microsoft.CommonLanguageServerProtocol.Framework;

#if CLASP_SOURCE_PACKAGE
[System.CodeDom.Compiler.GeneratedCode("Microsoft.CommonLanguageServerProtocol.Framework", "1.0")]
#endif
#if BINARY_COMPAT // TODO - Remove with https://github.com/dotnet/roslyn/issues/72251
public abstract class AbstractLspLogger : ILspLogger
#else
internal abstract class AbstractLspLogger : ILspLogger
#endif
{
    public abstract void LogDebug(string message, params object[] @params);
    public abstract void LogStartContext(string message, params object[] @params);
    public abstract void LogEndContext(string message, params object[] @params);
    public abstract void LogInformation(string message, params object[] @params);
    public abstract void LogWarning(string message, params object[] @params);
    public abstract void LogError(string message, params object[] @params);
    public abstract void LogException(Exception exception, string? message = null, params object[] @params);
}
