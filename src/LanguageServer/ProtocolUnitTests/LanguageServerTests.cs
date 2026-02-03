// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Roslyn.LanguageServer.Protocol;
using Xunit;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests;

public sealed class LanguageServerTests
{
    [Fact]
    public void TestObjectEquals_SameUri()
    {
        var uri1 = new DocumentUri("file:///c:/test.cs");
        var uri2 = new DocumentUri("file:///c:/test.cs");

        Assert.True(uri1.Equals(uri2));
        Assert.True(uri2.Equals(uri1));
    }

    [Fact]
    public void TestObjectEquals_EncodedAndUnencoded()
    {
        var unencoded = new DocumentUri("file:///c:/my folder/test.cs");
        var encoded = new DocumentUri("file:///c:/my%20folder/test.cs");

        Assert.True(unencoded.Equals(encoded));
        Assert.True(encoded.Equals(unencoded));
    }

    [Fact]
    public void TestObjectEquals_GitScheme()
    {
        var uri1 = new DocumentUri("git:/c:/repo/file.cs");
        var uri2 = new DocumentUri("git:/c:/repo/file.cs");

        Assert.True(uri1.Equals(uri2));
    }

    [Fact]
    public void TestObjectEquals_DifferentSchemes()
    {
        var fileUri = new DocumentUri("file:///c:/repo/file.cs");
        var gitUri = new DocumentUri("git:/c:/repo/file.cs");

        Assert.False(fileUri.Equals(gitUri));
    }

    [Fact]
    public void TestObjectEquals_UntitledScheme()
    {
        var uri1 = new DocumentUri("untitled:Untitled-1");
        var uri2 = new DocumentUri("untitled:Untitled-1");

        Assert.True(uri1.Equals(uri2));
    }

    [Fact]
    public void TestObjectEquals_Null()
    {
        var uri = new DocumentUri("file:///c:/test.cs");

        Assert.False(uri.Equals((object?)null));
        Assert.False(uri.Equals((DocumentUri?)null));
    }

    [Fact]
    public void TestObjectEquals_DifferentType()
    {
        var uri = new DocumentUri("file:///c:/test.cs");

        Assert.False(uri.Equals("file:///c:/test.cs"));
        Assert.False(uri.Equals(42));
    }
}
