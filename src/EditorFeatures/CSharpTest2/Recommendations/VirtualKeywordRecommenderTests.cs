﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.Recommendations;

[Trait(Traits.Feature, Traits.Features.KeywordRecommending)]
public sealed class VirtualKeywordRecommenderTests : KeywordRecommenderTests
{
    [Fact]
    public Task TestNotAtRoot_Interactive()
        => VerifyAbsenceAsync(SourceCodeKind.Script,
@"$$");

    [Fact]
    public Task TestNotAfterClass_Interactive()
        => VerifyAbsenceAsync(SourceCodeKind.Script,
            """
            class C { }
            $$
            """);

    [Fact]
    public Task TestNotAfterGlobalStatement_Interactive()
        => VerifyAbsenceAsync(SourceCodeKind.Script,
            """
            System.Console.WriteLine();
            $$
            """);

    [Fact]
    public Task TestNotAfterGlobalVariableDeclaration_Interactive()
        => VerifyAbsenceAsync(SourceCodeKind.Script,
            """
            int i = 0;
            $$
            """);

    [Fact]
    public Task TestNotInUsingAlias()
        => VerifyAbsenceAsync(
@"using Goo = $$");

    [Fact]
    public Task TestNotInGlobalUsingAlias()
        => VerifyAbsenceAsync(
@"global using Goo = $$");

    [Fact]
    public Task TestNotInEmptyStatement()
        => VerifyAbsenceAsync(AddInsideMethod(
@"$$"));

    [Fact]
    public async Task TestNotInCompilationUnit()
        => await VerifyAbsenceAsync(@"$$");

    [Fact]
    public Task TestNotAfterExtern()
        => VerifyAbsenceAsync("""
            extern alias Goo;
            $$
            """);

    [Fact]
    public Task TestNotAfterUsing()
        => VerifyAbsenceAsync("""
            using Goo;
            $$
            """);

    [Fact]
    public Task TestNotAfterGlobalUsing()
        => VerifyAbsenceAsync("""
            global using Goo;
            $$
            """);

    [Fact]
    public Task TestNotAfterNamespace()
        => VerifyAbsenceAsync("""
            namespace N {}
            $$
            """);

    [Fact]
    public Task TestNotAfterTypeDeclaration()
        => VerifyAbsenceAsync("""
            class C {}
            $$
            """);

    [Fact]
    public Task TestNotAfterDelegateDeclaration()
        => VerifyAbsenceAsync("""
            delegate void Goo();
            $$
            """);

    [Fact]
    public Task TestAfterMethodInClass()
        => VerifyKeywordAsync(
            """
            class C {
              void Goo() {}
              $$
            """);

    [Fact]
    public Task TestAfterFieldInClass()
        => VerifyKeywordAsync(
            """
            class C {
              int i;
              $$
            """);

    [Fact]
    public Task TestAfterPropertyInClass()
        => VerifyKeywordAsync(
            """
            class C {
              int i { get; }
              $$
            """);

    [Fact]
    public Task TestNotBeforeUsing()
        => VerifyAbsenceAsync(
            """
            $$
            using Goo;
            """);

    [Fact]
    public Task TestNotBeforeGlobalUsing()
        => VerifyAbsenceAsync(
            """
            $$
            global using Goo;
            """);

    [Fact]
    public Task TestNotAfterAssemblyAttribute()
        => VerifyAbsenceAsync("""
            [assembly: goo]
            $$
            """);

    [Fact]
    public Task TestNotAfterRootAttribute()
        => VerifyAbsenceAsync("""
            [goo]
            $$
            """);

    [Fact]
    public Task TestAfterNestedAttribute()
        => VerifyKeywordAsync(
            """
            class C {
              [goo]
              $$
            """);

    [Fact]
    public Task TestNotInsideStruct()
        => VerifyAbsenceAsync("""
            struct S {
               $$
            """);

    [Fact]
    public Task TestInsideInterface()
        => VerifyKeywordAsync("""
            interface I {
               $$
            """);

    [Fact]
    public Task TestInsideClass()
        => VerifyKeywordAsync(
            """
            class C {
               $$
            """);

    [Fact]
    public async Task TestNotAfterPartial()
        => await VerifyAbsenceAsync(@"partial $$");

    [Fact]
    public async Task TestNotAfterAbstract()
        => await VerifyAbsenceAsync(@"abstract $$");

    [Fact]
    public async Task TestNotAfterInternal()
        => await VerifyAbsenceAsync(@"internal $$");

    [Fact]
    public async Task TestNotAfterPublic()
        => await VerifyAbsenceAsync(@"public $$");

    [Fact]
    public async Task TestNotAfterStaticInternal()
        => await VerifyAbsenceAsync(@"static internal $$");

    [Fact]
    public async Task TestNotAfterInternalStatic()
        => await VerifyAbsenceAsync(@"internal static $$");

    [Fact]
    public async Task TestNotAfterInvalidInternal()
        => await VerifyAbsenceAsync(@"virtual internal $$");

    [Fact]
    public async Task TestNotAfterClass()
        => await VerifyAbsenceAsync(@"class $$");

    [Fact]
    public async Task TestNotAfterPrivate()
        => await VerifyAbsenceAsync(@"private $$");

    [Fact]
    public async Task TestNotAfterSealed()
        => await VerifyAbsenceAsync(@"sealed $$");

    [Fact]
    public async Task TestNotAfterStatic()
        => await VerifyAbsenceAsync(@"static $$");

    [Theory, CombinatorialData]
    public Task TestNotAfterNestedStatic([CombinatorialValues("class", "struct", "record", "record struct", "record class")] string declarationKind)
        => VerifyAbsenceAsync(declarationKind + """
            C {
               static $$
            """);

    [Fact]
    public Task TestAfterNestedStaticInInterface()
        => VerifyKeywordAsync("""
            interface C {
                static $$
            """);

    [Fact]
    public Task TestAfterNestedInternal()
        => VerifyKeywordAsync(
            """
            class C {
                internal $$
            """);

    [Fact]
    public Task TestNotAfterNestedPrivate()
        => VerifyAbsenceAsync("""
            class C {
                private $$
            """);

    [Fact]
    public async Task TestNotAfterDelegate()
        => await VerifyAbsenceAsync(@"delegate $$");

    [Fact]
    public Task TestNotAfterNestedAbstract()
        => VerifyAbsenceAsync("""
            class C {
                abstract $$
            """);

    [Fact]
    public Task TestNotAfterNestedVirtual()
        => VerifyAbsenceAsync("""
            class C {
                virtual $$
            """);

    [Fact]
    public Task TestNotAfterNestedOverride()
        => VerifyAbsenceAsync("""
            class C {
                override $$
            """);

    [Fact]
    public Task TestNotAfterNestedSealed()
        => VerifyAbsenceAsync("""
            class C {
                sealed $$
            """);

    [Fact]
    public Task TestNotInProperty()
        => VerifyAbsenceAsync(
            """
            class C {
                int Goo { $$
            """);

    [Fact]
    public Task TestNotInPropertyAfterAccessor()
        => VerifyAbsenceAsync(
            """
            class C {
                int Goo { get; $$
            """);

    [Fact]
    public Task TestNotInPropertyAfterAccessibility()
        => VerifyAbsenceAsync(
            """
            class C {
                int Goo { get; protected $$
            """);

    [Fact]
    public Task TestNotInPropertyAfterInternal()
        => VerifyAbsenceAsync(
            """
            class C {
                int Goo { get; internal $$
            """);

    [Fact]
    public Task TestAfterPrivateProtected()
        => VerifyKeywordAsync(
            """
            class C {
                private protected $$
            """);

    [Fact]
    public Task TestWithinExtension()
        => VerifyAbsenceAsync(
            """
            static class C
            {
                extension(string s)
                {
                    $$
                }
            }
            """, CSharpNextParseOptions);
}
