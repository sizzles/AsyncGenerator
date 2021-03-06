﻿using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace AsyncGenerator.Analyzation
{
	public interface IFunctionAnalyzationResult
	{
		/// <summary>
		/// Symbol of the function
		/// </summary>
		IMethodSymbol Symbol { get; }

		MethodConversion Conversion { get; }

		/// <summary>
		/// Get the syntax node of the function
		/// </summary>
		SyntaxNode GetNode();

		/// <summary>
		/// Get the syntax node of the function body
		/// </summary>
		SyntaxNode GetBodyNode();

		/// <summary>
		/// References of types that are used inside this function
		/// </summary>
		IReadOnlyList<ITypeReferenceAnalyzationResult> TypeReferences { get; }

		/// <summary>
		/// References to other methods that are invoked inside this function and are candidates to be async
		/// </summary>
		IReadOnlyList<IInvokeFunctionReferenceAnalyzationResult> MethodReferences { get; }

		/// <summary>
		/// Statements inside the function that were qualified as preconditions. Preconditions may be filled only for functions that 
		/// are going to be converted
		/// </summary>
		IReadOnlyList<StatementSyntax> Preconditions { get; }

		/// <summary>
		/// When true, the async keyword will be omitted
		/// </summary>
		bool OmitAsync { get; }

		/// <summary>
		/// When true, the method will be splitted into two. 
		/// </summary>
		bool SplitTail { get; }

		/// <summary>
		/// When true, the method will be wrapped in a try/catch block
		/// </summary>
		bool WrapInTryCatch { get; }
	}
}
