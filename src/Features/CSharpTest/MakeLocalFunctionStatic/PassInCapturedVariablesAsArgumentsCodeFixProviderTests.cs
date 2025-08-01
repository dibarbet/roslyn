﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.MakeLocalFunctionStatic;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.Diagnostics;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.MakeLocalFunctionStatic;

[Trait(Traits.Feature, Traits.Features.CodeActionsMakeLocalFunctionStatic)]
public sealed class PassInCapturedVariablesAsArgumentsCodeFixProviderTests : AbstractCSharpDiagnosticProviderBasedUserDiagnosticTest_NoEditor
{
    public PassInCapturedVariablesAsArgumentsCodeFixProviderTests(ITestOutputHelper logger)
      : base(logger)
    {
    }

    internal override (DiagnosticAnalyzer?, CodeFixProvider) CreateDiagnosticProviderAndFixer(Workspace workspace)
        => (null, new PassInCapturedVariablesAsArgumentsCodeFixProvider());

    private static readonly ParseOptions CSharp72ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp7_2);
    private static readonly ParseOptions CSharp8ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp8);

    [Fact]
    public Task TestMissingInCSharp7()
        => TestMissingAsync(
            """
            class C
            {
                int N(int x)
                {
                    return AddLocal();

                    static int AddLocal()
                    {
                        return [||]x + 1;
                    }        
                }
            }
            """, parameters: new TestParameters(parseOptions: CSharp72ParseOptions));

    [Fact]
    public Task TestMissingIfNoDiagnostic()
        => TestMissingAsync(
            """
            class C
            {
                int N(int x)
                {
                    return AddLocal();

                    int AddLocal()
                    {
                        return [||]x + 1;
                    }        
                }
            }
            """, parameters: new TestParameters(parseOptions: CSharp8ParseOptions));

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/38734")]
    public Task TestAvailableIfCapturesThisParameter1()
        => TestInRegularAndScriptAsync(
            """
            class C
            {
                int y = 0;

                int N(int x)
                {
                    return AddLocal();

                    static int AddLocal()
                    {
                        return [||]x + y;
                    }
                }
            }
            """,
            """
            class C
            {
                int y = 0;

                int N(int x)
                {
                    return AddLocal(this, x);

                    static int AddLocal(C @this, int x)
                    {
                        return x + @this.y;
                    }
                }
            }
            """, new(parseOptions: CSharp8ParseOptions));

    [Fact]
    public Task ShouldTriggerForCSharp8()
        => TestInRegularAndScriptAsync(
            """
            class C
            {
                int N(int x)
                {
                    return AddLocal();

                    static int AddLocal()
                    {
                        return [||]x + 1;
                    }
                }  
            }
            """,
            """
            class C
            {
                int N(int x)
                {
                    return AddLocal(x);

                    static int AddLocal(int x)
                    {
                        return x + 1;
                    }
                }  
            }
            """,
            new(parseOptions: CSharp8ParseOptions));

    [Fact]
    public Task TestMultipleVariables()
        => TestInRegularAndScriptAsync(
            """
            class C
            {
                int N(int x)
                {
                    int y = 10;
                    return AddLocal();

                    static int AddLocal()
                    {
                        return x + [||]y;
                    }
                }
            }
            """,
            """
            class C
            {
                int N(int x)
                {
                    int y = 10;
                    return AddLocal(x, y);

                    static int AddLocal(int x, int y)
                    {
                        return x + y;
                    }
                }
            }
            """, new(parseOptions: CSharp8ParseOptions));

    [Fact]
    public Task TestMultipleCalls()
        => TestInRegularAndScriptAsync(
            """
            class C
            {
                int N(int x)
                {
                    int y = 10;
                    return AddLocal() + AddLocal();

                    static int AddLocal()
                    {
                        return [||]x + y;
                    }
                }
            }
            """,
            """
            class C
            {
                int N(int x)
                {
                    int y = 10;
                    return AddLocal(x, y) + AddLocal(x, y);

                    static int AddLocal(int x, int y)
                    {
                        return x + y;
                    }
                }
            }
            """
, new(parseOptions: CSharp8ParseOptions));

    [Fact]
    public Task TestMultipleCallsWithExistingParameters()
        => TestInRegularAndScriptAsync(
            """
            class C
            {
                int N(int x)
                {
                    int y = 10;
                    var m = AddLocal(1, 2);
                    return AddLocal(m, m);

                    static int AddLocal(int a, int b)
                    {
                        return a + b + [||]x + y;
                    }
                }
            }
            """,
            """
            class C
            {
                int N(int x)
                {
                    int y = 10;
                    var m = AddLocal(1, 2, x, y);
                    return AddLocal(m, m, x, y);

                    static int AddLocal(int a, int b, int x, int y)
                    {
                        return a + b + x + y;
                    }
                }
            }
            """, new(parseOptions: CSharp8ParseOptions));

    [Fact]
    public Task TestRecursiveCall()
        => TestInRegularAndScriptAsync(
            """
            class C
            {
                int N(int x)
                {
                    int y = 10;
                    var m = AddLocal(1, 2);
                    return AddLocal(m, m);

                    static int AddLocal(int a, int b)
                    {
                        return AddLocal(a, b) + [||]x + y;
                    }
                }
            }
            """,
            """
            class C
            {
                int N(int x)
                {
                    int y = 10;
                    var m = AddLocal(1, 2, x, y);
                    return AddLocal(m, m, x, y);

                    static int AddLocal(int a, int b, int x, int y)
                    {
                        return AddLocal(a, b, x, y) + x + y;
                    }
                }
            }
            """, new(parseOptions: CSharp8ParseOptions));

    [Fact]
    public Task TestCallInArgumentList()
        => TestInRegularAndScriptAsync(
            """
            class C
            {
                int N(int x)
                {
                    int y = 10;
                    return AddLocal(AddLocal(1, 2), AddLocal(3, 4));

                    static int AddLocal(int a, int b)
                    {
                        return AddLocal(a, b) + [||]x + y;
                    }
                }
            }
            """,
            """
            class C
            {
                int N(int x)
                {
                    int y = 10;
                    return AddLocal(AddLocal(1, 2, x, y), AddLocal(3, 4, x, y), x, y);

                    static int AddLocal(int a, int b, int x, int y)
                    {
                        return AddLocal(a, b, x, y) + x + y;
                    }
                }
            }
            """, new(parseOptions: CSharp8ParseOptions));

    [Fact]
    public Task TestCallsWithNamedArguments()
        => TestInRegularAndScriptAsync(
            """
            class C
            {
                int N(int x)
                {
                    int y = 10;
                    var m = AddLocal(1, b: 2);
                    return AddLocal(b: m, a: m);

                    static int AddLocal(int a, int b)
                    {
                        return a + b + [||]x + y;
                    }
                }
            }
            """,
            """
            class C
            {
                int N(int x)
                {
                    int y = 10;
                    var m = AddLocal(1, b: 2, x: x, y: y);
                    return AddLocal(b: m, a: m, x: x, y: y);

                    static int AddLocal(int a, int b, int x, int y)
                    {
                        return a + b + x + y;
                    }
                }
            }
            """
, new(parseOptions: CSharp8ParseOptions));

    [Fact]
    public Task TestCallsWithDafaultValue()
        => TestInRegularAndScriptAsync(
            """
            class C
            {
                int N(int x)
                {
                    string y = ";
                    var m = AddLocal(1);
                    return AddLocal(b: m);

                    static int AddLocal(int a = 0, int b = 0)
                    {
                        return a + b + x + [||]y.Length;
                    }
                }
            }
            """,
            """
            class C
            {
                int N(int x)
                {
                    string y = ";
                    var m = AddLocal(1, x: x, y: y);
                    return AddLocal(b: m, x: x, y: y);

                    static int AddLocal(int a = 0, int b = 0, int x = 0, string y = null)
                    {
                        return a + b + x + y.Length;
                    }
                }
            }
            """
, new(parseOptions: CSharp8ParseOptions));

    [Fact]
    public Task TestWarningAnnotation()
        => TestInRegularAndScriptAsync(
            """
            class C
            {
                void N(int x)
                {
                    Func<int> del = AddLocal;

                    static int AddLocal()
                    {
                        return [||]x + 1;
                    }
                }  
            }
            """,
            """
            class C
            {
                void N(int x)
                {
                    Func<int> del = AddLocal;

                    {|Warning:static int AddLocal(int x)
                    {
                        return x + 1;
                    }|}
                }  
            }
            """,
            new(parseOptions: CSharp8ParseOptions));

    [Fact]
    public Task TestNonCamelCaseCapture()
        => TestInRegularAndScriptAsync(
            """
            class C
            {
                int N(int x)
                {
                    int Static = 0;
                    return AddLocal();

                    static int AddLocal()
                    {
                        return [||]Static + 1;
                    }
                }  
            }
            """,
            """
            class C
            {
                int N(int x)
                {
                    int Static = 0;
                    return AddLocal(Static);

                    static int AddLocal(int @static)
                    {
                        return @static + 1;
                    }
                }  
            }
            """,
            new(parseOptions: CSharp8ParseOptions));

    [Fact]
    public Task TestFixAll()
        => TestInRegularAndScriptAsync(
            """
            class C
            {
                int M(int x)
                {
                    int y = 10;
                    var m = AddLocal(1, 2);
                    return AddLocal(m, m);

                    static int AddLocal(int a, int b)
                    {
                        return a + b + x + y;
                    }
                }

                int N(int x)
                {
                    int y = 10;
                    return AddLocal(AddLocal(1, 2), AddLocal(3, 4));

                    static int AddLocal(int a, int b)
                    {
                        return AddLocal(a, b) + {|FixAllInDocument:|}x + y;
                    }
                }
            }
            """,
            """
            class C
            {
                int M(int x)
                {
                    int y = 10;
                    var m = AddLocal(1, 2, x, y);
                    return AddLocal(m, m, x, y);

                    static int AddLocal(int a, int b, int x, int y)
                    {
                        return a + b + x + y;
                    }
                }

                int N(int x)
                {
                    int y = 10;
                    return AddLocal(AddLocal(1, 2, x, y), AddLocal(3, 4, x, y), x, y);

                    static int AddLocal(int a, int b, int x, int y)
                    {
                        return AddLocal(a, b, x, y) + x + y;
                    }
                }
            }
            """, new(parseOptions: CSharp8ParseOptions));
}

