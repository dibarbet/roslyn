' Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports System.Composition
Imports System.Threading
Imports Microsoft.CodeAnalysis.CommentSelection
Imports Microsoft.CodeAnalysis.Host.Mef
Imports Microsoft.CodeAnalysis.Text

Namespace Microsoft.CodeAnalysis.VisualBasic.CommentSelection
    <ExportLanguageService(GetType(ICommentSelectionService), LanguageNames.VisualBasic), [Shared]>
    Friend Class VisualBasicCommentSelectionService
        Inherits AbstractCommentSelectionService

        Public Overrides ReadOnly Property SingleLineCommentString As String = "'"

        Public Overrides ReadOnly Property SupportsBlockComment As Boolean = False

        Public Overrides ReadOnly Property BlockCommentEndString As String
            Get
                Throw New NotSupportedException()
            End Get
        End Property

        Public Overrides ReadOnly Property BlockCommentStartString As String
            Get
                Throw New NotSupportedException()
            End Get
        End Property

        Public Overrides Function ToggleBlockComment(document As Document, selectedSpans As IEnumerable(Of TextSpan), cancellationToken As CancellationToken) As Task(Of Document)
            Throw New NotImplementedException()
        End Function
    End Class
End Namespace
