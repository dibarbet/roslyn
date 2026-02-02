// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Roslyn.LanguageServer.Protocol;
using Xunit;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests;

public sealed class DocumentUriTests
{
    [Theory]
    [InlineData(true, null, null)]
    [InlineData(false, "file://c:\\valid", null)]
    [InlineData(false, null, "file://c:\\valid")]
    [InlineData(true, "file://c:\\valid", "file://c:\\valid")]
    [InlineData(true, "file://c:\\valid", "file:///c:/valid")]
    [InlineData(true, "file://c:\\valid", "file://c:\\VALID")]
    [InlineData(false, "file://c:\\valid", "file://c:\\valid2")]
    public void TestUriEquality(bool areEqual, string? uriString1, string? uriString2)
    {
        var documentUri1 = uriString1 != null ? new DocumentUri(uriString1) : null;
        var documentUri2 = uriString2 != null ? new DocumentUri(uriString2) : null;

        Assert.True(areEqual == (documentUri1 == documentUri2));
        Assert.True(areEqual != (documentUri1 != documentUri2));
    }

    [Fact]
    public void TestObjectEquals_SameUri()
    {
        var uri1 = new DocumentUri("file:///c:/test.cs");
        var uri2 = new DocumentUri("file:///c:/test.cs");

        Assert.True(uri1.Equals((object)uri2));
        Assert.True(uri2.Equals((object)uri1));
    }

    [Fact]
    public void TestObjectEquals_DifferentCasing()
    {
        var upperCase = new DocumentUri("file:///C:/Test.cs");
        var lowerCase = new DocumentUri("file:///c:/test.cs");

        // File URIs are case-insensitive on Windows
        Assert.True(upperCase.Equals((object)lowerCase));
        Assert.True(lowerCase.Equals((object)upperCase));
    }

    [Fact]
    public void TestObjectEquals_Null()
    {
        var uri = new DocumentUri("file:///c:/test.cs");

        Assert.False(uri.Equals((object?)null));
    }

    [Fact]
    public void TestObjectEquals_DifferentType()
    {
        var uri = new DocumentUri("file:///c:/test.cs");

        Assert.False(uri.Equals("file:///c:/test.cs"));
        Assert.False(uri.Equals(42));
    }
}
