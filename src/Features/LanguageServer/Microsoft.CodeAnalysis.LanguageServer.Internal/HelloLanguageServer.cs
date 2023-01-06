// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Composition;

namespace Microsoft.CodeAnalysis.LanguageServer.Internal;

/// <summary>
/// Placeholder code to show that something is in the dll.
/// Delete this as soon as we have anything else in this project.
/// </summary>
[Export(typeof(HelloLanguageServer)), Shared]
internal class HelloLanguageServer
{
    [ImportingConstructor]
    [Obsolete("This exported object must be obtained through the MEF export provider.", error: true)]
    public HelloLanguageServer() { }

    public string GetMessage()
    {
        return $"Hello from {this.GetType().Assembly.FullName}";
    }
}
