﻿' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Imports System.Threading
Imports Microsoft.CodeAnalysis.Completion.Providers
Imports Microsoft.CodeAnalysis.VisualBasic.Extensions.ContextQuery
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax

Namespace Microsoft.CodeAnalysis.VisualBasic.Completion.KeywordRecommenders.Statements
    ''' <summary>
    ''' Recommends the "Step" keyword in a For statement.
    ''' </summary>
    Friend Class StepKeywordRecommender
        Inherits AbstractKeywordRecommender

        Protected Overrides Function RecommendKeywords(context As VisualBasicSyntaxContext, cancellationToken As CancellationToken) As IEnumerable(Of RecommendedKeyword)
            If context.SyntaxTree.IsFollowingCompleteExpression(Of ForStatementSyntax)(
                context.Position,
                context.TargetToken,
                Function(forStatement) forStatement.ToValue,
                cancellationToken,
                allowImplicitLineContinuation:=False) Then

                Return SpecializedCollections.SingletonEnumerable(New RecommendedKeyword("Step", VBFeaturesResources.Specifies_how_much_to_increment_between_each_loop_iteration))
            Else
                Return SpecializedCollections.EmptyEnumerable(Of RecommendedKeyword)()
            End If
        End Function
    End Class
End Namespace
