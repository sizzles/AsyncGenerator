﻿using System;
using System.Linq;
using AsyncGenerator.Extensions;
using AsyncGenerator.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AsyncGenerator.Analyzation.Internal
{
	internal partial class ProjectAnalyzer
	{
		/// <summary>
		/// Set the method conversion to Ignore for all method data that are inside the given document and can not be
		/// converted due to the language limitations or an already existing async counterpart.
		/// </summary>
		/// <param name="documentData">The document data to be pre-analyzed</param>
		private void PreAnalyzeDocumentData(DocumentData documentData)
		{
			foreach (var typeNode in documentData.Node
				.DescendantNodes()
				.OfType<TypeDeclarationSyntax>())
			{
				var typeData = documentData.GetOrCreateTypeData(typeNode);
				typeData.Conversion = _configuration.TypeConversionFunction(typeData.Symbol);
				PreAnalyzeType(typeData);

				foreach (var methodNode in typeNode
					.DescendantNodes()
					.OfType<MethodDeclarationSyntax>())
				{
					var methodData = documentData.GetOrCreateMethodData(methodNode, typeData);
					if (typeData.Conversion == TypeConversion.Ignore)
					{
						methodData.Conversion = MethodConversion.Ignore;
					}
					else
					{
						PreAnalyzeMethodData(methodData);
					}
					
					foreach (var funNode in methodNode
						.DescendantNodes()
						.OfType<AnonymousFunctionExpressionSyntax>())
					{
						var funData = documentData.GetOrCreateAnonymousFunctionData(funNode, methodData);
						if (typeData.Conversion == TypeConversion.Ignore)
						{
							methodData.Conversion = MethodConversion.Ignore;
						}
						else
						{
							PreAnalyzeAnonymousFunction(funData, documentData.SemanticModel);
						}
					}
				}
			}
		}

		private void PreAnalyzeType(TypeData typeData)
		{
			if (typeData.Conversion == TypeConversion.Ignore)
			{
				return;
			}
			typeData.IsPartial = typeData.Node.IsPartial();
		}

		private void PreAnalyzeMethodData(MethodData methodData)
		{
			var methodSymbol = methodData.Symbol;
			methodData.Conversion = _configuration.MethodConversionFunction(methodSymbol);
			if (methodData.Conversion == MethodConversion.Ignore)
			{
				Logger.Debug($"Method {methodSymbol} will be ignored because of MethodConversionFunction");
				return;
			}

			var forceAsync = methodData.Conversion == MethodConversion.ToAsync;
			var log = forceAsync ? Logger.Warn : (Action<object>)Logger.Debug;
			if (methodSymbol.IsAsync || methodSymbol.Name.EndsWith("Async"))
			{
				log($"Symbol {methodSymbol} is already async");
				methodData.Conversion = MethodConversion.Ignore;
				methodData.IsAsync = true;
				return;
			}
			if (!ProjectData.Contains(methodSymbol))
			{
				log($"Method {methodSymbol} is external and cannot be made async");
				methodData.Conversion = MethodConversion.Ignore;
				return;
			}
			if (methodSymbol.MethodKind != MethodKind.Ordinary && methodSymbol.MethodKind != MethodKind.ExplicitInterfaceImplementation)
			{
				log($"Method {methodSymbol} is a {methodSymbol.MethodKind} and cannot be made async");
				methodData.Conversion = MethodConversion.Ignore;
				return;
			}

			if (methodSymbol.Parameters.Any(o => o.RefKind == RefKind.Out))
			{
				log($"Method {methodSymbol} has out parameters and cannot be made async");
				methodData.Conversion = MethodConversion.Ignore;
				return;
			}

			// Check if explicitly implements external interfaces
			if (methodSymbol.MethodKind == MethodKind.ExplicitInterfaceImplementation)
			{
				foreach (var interfaceMember in methodSymbol.ExplicitInterfaceImplementations)
				{
					if (methodSymbol.ContainingAssembly.Name != interfaceMember.ContainingAssembly.Name)
					{
						methodData.ExternalRelatedMethods.TryAdd(interfaceMember);

						// Check if the interface member has an async counterpart
						var asyncConterPart = interfaceMember.ContainingType.GetMembers()
							.OfType<IMethodSymbol>()
							.Where(o => o.Name == methodSymbol.Name + "Async")
							.SingleOrDefault(o => methodSymbol.IsAsyncCounterpart(o, true, false, false));

						if (asyncConterPart == null)
						{
							log($"Method {methodSymbol} implements an external interface {interfaceMember} and cannot be made async");
							methodData.Conversion = MethodConversion.Ignore;
							return;
						}
						methodData.ExternalAsyncMethods.TryAdd(asyncConterPart);
					}
					else
					{
						methodData.ImplementedInterfaces.TryAdd(interfaceMember);
					}
					//var syntax = interfaceMember.DeclaringSyntaxReferences.FirstOrDefault();
					//if (!CanProcessSyntaxReference(syntax))
					//{
					//	continue;
					//}

				}
			}

			// Check if the method is overriding an external method
			var overridenMethod = methodSymbol.OverriddenMethod;
			while (overridenMethod != null)
			{
				if (methodSymbol.ContainingAssembly.Name != overridenMethod.ContainingAssembly.Name)
				{
					methodData.ExternalRelatedMethods.TryAdd(overridenMethod);
					// Check if the external member has an async counterpart that is not implemented in the current type (missing member)
					var asyncConterPart = overridenMethod.ContainingType.GetMembers()
						.OfType<IMethodSymbol>()
						.Where(o => o.Name == methodSymbol.Name + "Async" && !o.IsSealed && (o.IsVirtual || o.IsAbstract || o.IsOverride))
						.SingleOrDefault(o => methodSymbol.IsAsyncCounterpart(o, true, false, false));
					if (asyncConterPart == null)
					{
						log(
							$"Method {methodSymbol} overrides an external method {overridenMethod} that has not an async counterpart... method will not be converted");
						methodData.Conversion = MethodConversion.Ignore;
						return;
						//if (!asyncMethods.Any() || (asyncMethods.Any() && !overridenMethod.IsOverride && !overridenMethod.IsVirtual))
						//{
						//	Logger.Warn($"Method {methodSymbol} overrides an external method {overridenMethod} and cannot be made async");
						//	return MethodSymbolAnalyzeResult.Invalid;
						//}
					}
					methodData.ExternalAsyncMethods.TryAdd(asyncConterPart);
				}
				else
				{
					methodData.OverridenMethods.TryAdd(overridenMethod);
				}
				//var syntax = overridenMethod.DeclaringSyntaxReferences.SingleOrDefault();
				//else if (CanProcessSyntaxReference(syntax))
				//{
				//	methodData.OverridenMethods.TryAdd(overridenMethod);
				//}
				if (overridenMethod.OverriddenMethod != null)
				{
					overridenMethod = overridenMethod.OverriddenMethod;
				}
				else
				{
					break;
				}
			}
			methodData.BaseOverriddenMethod = overridenMethod;

			// Check if the method is implementing an external interface, if true skip as we cannot modify externals
			// FindImplementationForInterfaceMember will find the first implementation method starting from the deepest base class
			var type = methodSymbol.ContainingType;
			foreach (var interfaceMember in type.AllInterfaces
												.SelectMany(
													o => o.GetMembers(methodSymbol.Name)
														  .Where(
															  m =>
															  {
																  // Find out if the method implements the interface member or an override 
																  // method that implements it
																  var impl = type.FindImplementationForInterfaceMember(m);
																  return methodSymbol.Equals(impl) || methodData.OverridenMethods.Any(ov => ov.Equals(impl));
															  }
															))
														  .OfType<IMethodSymbol>())
			{
				if (methodSymbol.ContainingAssembly.Name != interfaceMember.ContainingAssembly.Name)
				{
					methodData.ExternalRelatedMethods.TryAdd(interfaceMember);

					// Check if the member has an async counterpart that is not implemented in the current type (missing member)
					var asyncConterPart = interfaceMember.ContainingType.GetMembers()
						.OfType<IMethodSymbol>()
						.Where(o => o.Name == methodSymbol.Name + "Async")
						.SingleOrDefault(o => methodSymbol.IsAsyncCounterpart(o, true, false, false));
					if (asyncConterPart == null)
					{
						log($"Method {methodSymbol} implements an external interface {interfaceMember} and cannot be made async");
						methodData.Conversion = MethodConversion.Ignore;
						return;
					}
					methodData.ExternalAsyncMethods.TryAdd(asyncConterPart);
				}
				else
				{
					methodData.ImplementedInterfaces.TryAdd(interfaceMember);
				}
				//var syntax = interfaceMember.DeclaringSyntaxReferences.SingleOrDefault();
				//if (!CanProcessSyntaxReference(syntax))
				//{
				//	continue;
				//}

			}

			// Verify if there is already an async counterpart for this method
			//TODO: this is not correct when generating methods with a cancellation token as here we do not know
			// if the generated method will have the cancellation token parameter or not
			var searchOptions = AsyncCounterpartsSearchOptions.EqualParameters | AsyncCounterpartsSearchOptions.IgnoreReturnType;
			if (_configuration.UseCancellationTokenOverload)
			{
				searchOptions |= AsyncCounterpartsSearchOptions.HasCancellationToken;
			}
			var asyncCounterparts = GetAsyncCounterparts(methodSymbol.OriginalDefinition, searchOptions).ToList();
			if (asyncCounterparts.Any())
			{
				if (!_configuration.UseCancellationTokenOverload && asyncCounterparts.Count > 1)
				{
					throw new InvalidOperationException($"Method {methodSymbol} has more than one async counterpart");
				}
				// We shall get a maximum of two async counterparts when the HasCancellationToken flag is used
				if (_configuration.UseCancellationTokenOverload && asyncCounterparts.Count > 2)
				{
					throw new InvalidOperationException($"Method {methodSymbol} has more than two async counterparts");
				}

				foreach (var asyncCounterpart in asyncCounterparts)
				{
					// Check if the async counterpart has a cancellation token
					if (asyncCounterpart.Parameters.Length > methodSymbol.Parameters.Length)
					{
						methodData.AsyncCounterpartWithTokenSymbol = asyncCounterpart;
					}
					else
					{
						methodData.AsyncCounterpartSymbol = asyncCounterpart;
					}
				}

				if (
					(_configuration.UseCancellationTokenOverload && asyncCounterparts.Count == 2) ||
					(!_configuration.UseCancellationTokenOverload && asyncCounterparts.Count == 1)
				)
				{
					log($"Method {methodSymbol} has already an async counterpart {asyncCounterparts.First()}");
					methodData.Conversion = MethodConversion.Ignore;
					return;
				}
			}
		}

		private void PreAnalyzeAnonymousFunction(AnonymousFunctionData functionData, SemanticModel semanticModel)
		{
			var funcionSymbol = functionData.Symbol;
			var forceAsync = functionData.MethodData.Conversion == MethodConversion.ToAsync;
			var log = forceAsync ? Logger.Warn : (Action<object>)Logger.Debug;
			if (funcionSymbol.IsAsync)
			{
				log($"Anonymous function inside method {functionData.MethodData.Symbol} is already async");
				functionData.Conversion = MethodConversion.Ignore;
				functionData.IsAsync = true;
				return;
			}
			if (funcionSymbol.Parameters.Any(o => o.RefKind == RefKind.Out))
			{
				log($"Anonymous function inside method {functionData.MethodData.Symbol} has out parameters and cannot be made async");
				functionData.Conversion = MethodConversion.Ignore;
				return;
			}

			if (!functionData.Node.Parent.IsKind(SyntaxKind.Argument))
			{
				log($"Anonymous function inside method {functionData.MethodData.Symbol} is not passed as an argument but as a {Enum.GetName(typeof(SyntaxKind),functionData.Node.Parent.Kind())} which is not supported");
				functionData.Conversion = MethodConversion.Ignore;
				return;
			}
			else
			{
				var invocationNode = functionData.Node.Ancestors()
				.TakeWhile(o => !o.IsKind(SyntaxKind.MethodDeclaration))
				.OfType<InvocationExpressionSyntax>()
				.First();
				var argumentNode = (ArgumentSyntax)functionData.Node.Parent;
				var argumentListNode = (ArgumentListSyntax)argumentNode.Parent;
				var index = argumentListNode.Arguments.IndexOf(argumentNode);
				var symbol = (IMethodSymbol)semanticModel.GetSymbolInfo(invocationNode.Expression).Symbol;
				functionData.ArgumentOfMethod = new Tuple<IMethodSymbol, int>(symbol, index);
			}

		}
	}
}
