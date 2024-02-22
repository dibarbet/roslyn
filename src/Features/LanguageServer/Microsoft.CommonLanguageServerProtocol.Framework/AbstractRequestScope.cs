// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace Microsoft.CommonLanguageServerProtocol.Framework;

#if CLASP_SOURCE_PACKAGE
[System.CodeDom.Compiler.GeneratedCode("Microsoft.CommonLanguageServerProtocol.Framework", "1.0")]
#endif
#if BINARY_COMPAT // TODO - Remove with https://github.com/dotnet/roslyn/issues/72251
public abstract class AbstractRequestScope(string name) : IDisposable
#else
internal abstract class AbstractRequestScope(string name) : IDisposable
#endif
{
    public string Name { get; } = name;

    public abstract void RecordCancellation();
    public abstract void RecordException(Exception exception);
    public abstract void RecordWarning(string message);

    public abstract void RecordExecutionStart();

    public abstract void Dispose();
}
