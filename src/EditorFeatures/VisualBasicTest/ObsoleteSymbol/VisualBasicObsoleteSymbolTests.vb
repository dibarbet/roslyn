' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Imports Microsoft.CodeAnalysis.Editor.UnitTests.ObsoleteSymbol

Namespace Microsoft.CodeAnalysis.Editor.VisualBasic.UnitTests.ObsoleteSymbol
    Public Class VisualBasicObsoleteSymbolTests
        Inherits AbstractObsoleteSymbolTests

        Protected Overrides Function CreateWorkspace(markup As String) As EditorTestWorkspace
            Return EditorTestWorkspace.CreateVisualBasic(markup)
        End Function

        <Theory>
        <InlineData("Class")>
        <InlineData("Structure")>
        <InlineData("Interface")>
        <InlineData("Module")>
        <InlineData("Enum")>
        Public Async Function TestObsoleteTypeDefinition(keyword As String) As Task
            Await TestAsync(
                $"
                <System.Obsolete>
                {keyword} [|ObsoleteType|]
                End {keyword}

                {keyword} NonObsoleteType
                End {keyword}
                ")
        End Function

        <Fact>
        Public Async Function TestObsoleteDelegateTypeDefinition() As Task
            Await TestAsync(
                "
                <System.Obsolete>
                Delegate Sub [|ObsoleteType|]()

                Delegate Sub NonObsoleteType()
                ")
        End Function

        <Fact>
        Public Async Function TestDeclarationAndUseOfObsoleteAlias() As Task
            Await TestAsync(
                "
                Imports [|ObsoleteAlias|] = [|ObsoleteType|]

                <System.Obsolete>
                Class [|ObsoleteType|]
                End Class

                ''' <seealso cref=""[|ObsoleteType|]""/>
                ''' <seealso cref=""[|ObsoleteAlias|]""/>
                Class NonObsoleteType
                    Dim field As [|ObsoleteAlias|] = New [|ObsoleteType|]()
                End Class
                ")
        End Function

        <Fact>
        Public Async Function TestParametersAndReturnTypes() As Task
            Await TestAsync(
                "
                <System.Obsolete>
                Class [|ObsoleteType|]
                End Class

                Class NonObsoleteType
                    Function Method(arg As [|ObsoleteType|]) As [|ObsoleteType|]
                        Return New [|ObsoleteType|]()
                    End Function

                    Dim field As System.Func(Of [|ObsoleteType|], [|ObsoleteType|]) = Function(arg As [|ObsoleteType|]) New [|ObsoleteType|]()
                End Class
                ")
        End Function

        <Fact>
        Public Async Function TestImplicitType() As Task
            Await TestAsync(
                "
                <System.Obsolete>
                Class [|ObsoleteType|]
                End Class

                Class NonObsoleteType
                    Sub Method()
                        Dim t1 As New [|ObsoleteType|]()
                        [|Dim|] t2 = New [|ObsoleteType|]()
                        Dim t3 As [|ObsoleteType|] = New [|ObsoleteType|]()
                        [|Dim|] t4 = CreateObsoleteType()
                        Dim t5 = NameOf([|ObsoleteType|])
                    End Sub

                    Function CreateObsoleteType() As [|ObsoleteType|]
                        Return New [|ObsoleteType|]()
                    End Function
                End Class
                ")
        End Function

        <Fact>
        Public Async Function TestExtensionMethods() As Task
            Await TestAsync(
                "
                <System.Obsolete>
                Module [|ObsoleteType|]
                    <System.Runtime.CompilerServices.Extension>
                    Public Shared Sub ObsoleteMember1(ignored As C)
                    End Sub

                    <System.Obsolete>
                    <System.Runtime.CompilerServices.Extension>
                    Public Shared Sub [|ObsoleteMember2|](ignored As C)
                    End Sub
                End Module

                Class C
                    Sub Method()
                        Me.ObsoleteMember1()
                        Me.[|ObsoleteMember2|]()
                        [|ObsoleteType|].ObsoleteMember1(Me)
                        [|ObsoleteType|].[|ObsoleteMember2|](Me)
                    End Sub
                End Class
                ")
        End Function

        <Fact>
        Public Async Function TestGenerics() As Task
            Await TestAsync(
                "
                <System.Obsolete>
                Class [|ObsoleteType|]
                End Class

                <System.Obsolete>
                Structure [|ObsoleteValueType|]
                End Structure

                Class G(Of T)
                End Class

                Class C
                    Sub M(Of T)()
                    End Sub

                    ''' <summary>
                    ''' Visual Basic, unlike C#, resolves concrete type names in generic argument positions in doc
                    ''' comment references.
                    ''' </summary>
                    ''' <seealso cref=""G(Of [|ObsoleteType|])""/>
                    Sub Method()
                        Dim x1 = New G(Of [|ObsoleteType|])()
                        Dim x2 = New G(Of G(Of [|ObsoleteType|]))()
                        M(Of [|ObsoleteType|])()
                        M(Of G(Of [|ObsoleteType|]))()
                        M(Of G(Of G(Of [|ObsoleteType|])))()

                        ' Mark 'Dim' as obsolete even when it points to Nullable(Of T) where T is obsolete
                        [|Dim|] nullableValue = CreateNullableValueType()
                    End Sub

                    Function CreateNullableValueType() As [|ObsoleteValueType|]?
                        Return New [|ObsoleteValueType|]()
                    End Function
                End Class
                ")
        End Function
    End Class
End Namespace
