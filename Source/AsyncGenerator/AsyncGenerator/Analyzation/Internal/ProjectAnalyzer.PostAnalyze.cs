﻿using System.Collections.Generic;
using System.Linq;
using AsyncGenerator.Extensions;
using AsyncGenerator.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AsyncGenerator.Analyzation.Internal
{
	internal partial class ProjectAnalyzer
	{
		/// <summary>
		/// Set all method data dependencies to be also async
		/// </summary>
		/// <param name="asyncMethodData">Method data that is marked to be async</param>
		/// <param name="toProcessMethodData">All method data that needs to be processed</param>
		private void PostAnalyzeAsyncMethodData(MethodData asyncMethodData, ISet<MethodData> toProcessMethodData)
		{
			if (!toProcessMethodData.Contains(asyncMethodData))
			{
				return;
			}
			var processingMetodData = new Queue<MethodData>();
			processingMetodData.Enqueue(asyncMethodData);
			while (processingMetodData.Any())
			{
				var currentMethodData = processingMetodData.Dequeue();
				toProcessMethodData.Remove(currentMethodData);
				foreach (var depFunctionData in currentMethodData.Dependencies)
				{
					var depMethodData = depFunctionData as MethodData;
					if (depMethodData != null)
					{
						if (!toProcessMethodData.Contains(depMethodData))
						{
							continue;
						}
						processingMetodData.Enqueue(depMethodData);
					}
					if (depFunctionData.Conversion == MethodConversion.Ignore)
					{
						Logger.Info($"Ignored method {depFunctionData.Symbol} has a method invocation that can be async");
						continue;
					}
					depFunctionData.Conversion = MethodConversion.ToAsync;

					// We need to update the CancellationTokenRequired for all invocations of the current method
					foreach (var depFunctionRefData in depFunctionData.InvokedMethodReferences.Where(o => o.ReferenceFunctionData == currentMethodData))
					{
						depFunctionRefData.CancellationTokenRequired = currentMethodData.CancellationTokenRequired;
					}
					// Propagate the CancellationTokenRequired for the dependency method data
					if (depMethodData != null)
					{
						depMethodData.CancellationTokenRequired = currentMethodData.CancellationTokenRequired;
					}
				}
			}
		}

		/// <summary>
		/// Skip wrapping a method into a try/catch only when we have one statement (except preconditions) that is an invocation
		/// which returns a Task. This statement must have only one invocation.
		/// </summary>
		/// <param name="methodData"></param>
		private void CalculateWrapInTryCatch(MethodData methodData)
		{
			var methodDataBody = methodData.Node.Body;
			if (methodDataBody == null)
			{
				return;
			}
			if (methodDataBody.Statements.Count != methodData.Preconditions.Count + 1)
			{
				methodData.WrapInTryCatch = true;
				return;
			}
			var statements = methodDataBody.Statements
				.First(o => !methodData.Preconditions.Contains(o))
				.DescendantNodesAndSelf()
				.OfType<StatementSyntax>()
				.ToList();
			if (statements.Count != 1)
			{
				methodData.WrapInTryCatch = true;
				return;
			}
			var lastStatement = statements[0];
			var invocationExps = lastStatement?.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
			if (invocationExps?.Count != 1)
			{
				methodData.WrapInTryCatch = true;
				return;
			}
			var invocationExpr = invocationExps[0];
			var refData = methodData.InvokedMethodReferences.FirstOrDefault(o => o.ReferenceNode == invocationExpr);
			if (refData == null)
			{
				methodData.WrapInTryCatch = true;
				return;
			}
			if (refData.GetConversion() == ReferenceConversion.Ignore || refData.ReferenceAsyncSymbols.Any(o => o.ReturnsVoid || !o.ReturnType.IsTaskType()))
			{
				methodData.WrapInTryCatch = true;
			}
		}

		/// <summary>
		/// Calculates the final conversion for all currently not ignored method data
		/// </summary>
		/// <param name="documentData">All project documents</param>
		private void PostAnalyze(IEnumerable<DocumentData> documentData)
		{
			// If a type data is ignored then also its method data are ignored
			var allTypeData = documentData
				.SelectMany(o => o.GetAllTypeDatas())
				.Where(o => o.Conversion != TypeConversion.Ignore)
				.ToList();
			var toProcessMethodData = new HashSet<MethodData>(allTypeData
				.SelectMany(o => o.Methods.Values.Where(m => m.Conversion != MethodConversion.Ignore)));
			//TODO: optimize steps for better performance

			// 0. Step - If cancellation tokens are enabled we should start from methods that requires a cancellation token in order to correctly propagate CancellationTokenRequired
			// to dependency methods
			if (_configuration.UseCancellationTokenOverload)
			{
				var tokenMethodDatas = toProcessMethodData.Where(o => o.CancellationTokenRequired).ToList();
				foreach (var tokenMethodData in tokenMethodDatas)
				{
					if (toProcessMethodData.Count == 0)
					{
						break;
					}
					tokenMethodData.Conversion = MethodConversion.ToAsync;
					PostAnalyzeAsyncMethodData(tokenMethodData, toProcessMethodData);
				}
			}
			
			// 1. Step - Go through all async methods and set their dependencies to be also async
			// TODO: should we start from the bottom/leaf method that is async? how do we know if the method is a leaf (consider circular calls)?
			var asyncMethodDatas = toProcessMethodData.Where(o => o.Conversion == MethodConversion.ToAsync).ToList();
			foreach (var asyncMethodData in asyncMethodDatas)
			{
				if (toProcessMethodData.Count == 0)
				{
					break;
				}
				PostAnalyzeAsyncMethodData(asyncMethodData, toProcessMethodData);
			}

			// 2. Step - Go through remaining methods and set them to be async if there is at least one method invocation that will get converted
			// TODO: should we start from the bottom/leaf method that is async? how do we know if the method is a leaf (consider circular calls)?
			var remainingMethodData = toProcessMethodData.ToList();
			foreach (var methodData in remainingMethodData)
			{
				if (methodData.InvokedMethodReferences.Any(o => o.GetConversion() == ReferenceConversion.ToAsync))
				{
					if (methodData.Conversion == MethodConversion.Ignore)
					{
						Logger.Info($"Ignored method {methodData.Symbol} has a method invocation that can be async");
						continue;
					}
					methodData.Conversion = MethodConversion.ToAsync;
					// Set all dependencies to be async for the newly discovered async method
					PostAnalyzeAsyncMethodData(methodData, toProcessMethodData);
					if (toProcessMethodData.Count == 0)
					{
						break;
					}
				}
			}

			// 3. Step - Mark all remaining method to be ignored
			foreach (var methodData in toProcessMethodData)
			{
				methodData.Conversion = MethodConversion.Ignore;
			}

			// 4. Step - Calculate the final type conversion
			foreach (var typeData in allTypeData)
			{
				if (typeData.Conversion != TypeConversion.Unknown)
				{
					continue;
				}
				// A type can be ignored only if it has no async methods that will get converted
				if (typeData.Methods.Values.All(o => o.Conversion == MethodConversion.Ignore))
				{
					typeData.Conversion = TypeConversion.Ignore;
				}
				else
				{
					typeData.Conversion = TypeConversion.Partial;
				}
			}

			// 5. Step - For all async methods check for preconditions. Search only statements that its end location is lower that the first async method reference
			foreach (var methodData in allTypeData.Where(o => o.Conversion != TypeConversion.Ignore)
				.SelectMany(o => o.Methods.Values.Where(m => m.Conversion != MethodConversion.Ignore)))
			{
				if (methodData.GetBodyNode() == null)
				{
					continue;
				}

				var asyncMethodReferences = methodData.InvokedMethodReferences
					.Where(o => o.GetConversion() == ReferenceConversion.ToAsync)
					.ToList();
				// Calculate the final reference AwaitInvocation, we can skip await if all async invocations are returned and the return type matches
				// or we have only one async invocation that is the last to be invoked
				var canSkipAwaits = true;
				foreach (var methodReference in methodData.InvokedMethodReferences)
				{
					if (methodReference.GetConversion() == ReferenceConversion.Ignore)
					{
						methodReference.AwaitInvocation = false;
						continue;
					}

					if (!methodReference.UseAsReturnValue && !methodReference.LastInvocation)
					{
						canSkipAwaits = false;
						break;
					}
					var functionData = methodReference.FunctionData;

					if (methodReference.LastInvocation && functionData.Symbol.ReturnsVoid && (
							(methodReference.ReferenceAsyncSymbols.Any() && methodReference.ReferenceAsyncSymbols.All(o => o.ReturnType.IsTaskType())) ||
							methodReference.ReferenceFunctionData?.Conversion == MethodConversion.ToAsync
						))
					{
						continue;
					}

					var isReturnTypeTask = methodReference.ReferenceSymbol.ReturnType.IsTaskType();
					// We need to check the return value of the async counterpart
					// eg. Task<IList<string>> to Task<IEnumerable<string>>, Task<long> -> Task<int> are not valid
					// eg. Task<int> to Task is valid
					if (!isReturnTypeTask &&
						(
							(
								methodReference.ReferenceAsyncSymbols.Any() &&
								!methodReference.ReferenceAsyncSymbols.All(o =>
								{
									var returnType = o.ReturnType as INamedTypeSymbol;
									if (returnType == null || !returnType.IsGenericType)
									{
										return o.ReturnType.Equals(functionData.Symbol.ReturnType);
									}
									return returnType.TypeArguments.First().Equals(functionData.Symbol.ReturnType);
								})
								) ||
							(
								methodReference.ReferenceFunctionData != null &&
								!methodReference.ReferenceFunctionData.Symbol.ReturnType.Equals(functionData.Symbol.ReturnType)
								)
							)
						)
					{
						canSkipAwaits = false;
						break;
					}
				}
				if (canSkipAwaits)
				{
					foreach (var methodReference in asyncMethodReferences)
					{
						methodReference.AwaitInvocation = false;
						methodReference.UseAsReturnValue = true;
					}
				}

				if (methodData.Node.Body != null)
				{
					// Some async methods may not have any async invocations because were forced to be async (overloads)
					var methodRefSpan = asyncMethodReferences
						.Select(o => o.ReferenceLocation.Location)
						.OrderBy(o => o.SourceSpan.Start)
						.FirstOrDefault();
					var semanticModel = methodData.TypeData.NamespaceData.DocumentData.SemanticModel;
					// Search for preconditions until a statement has not been qualified as a precondition or we encounter an async invocation
					foreach (var statement in methodData.Node.Body.Statements)
					{
						if (methodRefSpan != null && statement.Span.End > methodRefSpan.SourceSpan.Start)
						{
							break;
						}
						if (!_configuration.PreconditionCheckers.Any(o => o.IsPrecondition(statement, semanticModel)))
						{
							break;
						}
						methodData.Preconditions.Add(statement);
					}

					// A method shall be tail splitted when has at least one precondition and there is at least one awaitable invocation
					if (methodData.Preconditions.Any() && methodData.InvokedMethodReferences.Any(o => o.AwaitInvocation == true))
					{
						methodData.SplitTail = true;
					}
				}

				// The async keyword shall be omitted when the method does not have any awaitable invocation
				if (!methodData.InvokedMethodReferences.Any(o => o.GetConversion() == ReferenceConversion.ToAsync && o.AwaitInvocation == true))
				{
					methodData.OmitAsync = true;
				}
				// When the async keyword is omitted we need to calculate if the method body shall be wrapped in a try/catch block
				if (methodData.OmitAsync)
				{
					CalculateWrapInTryCatch(methodData);
				}
				
			}
		}
	}
}
