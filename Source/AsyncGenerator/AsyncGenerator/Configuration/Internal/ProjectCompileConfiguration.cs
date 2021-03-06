﻿using System;

namespace AsyncGenerator.Configuration.Internal
{
	internal class ProjectCompileConfiguration : IProjectCompileConfiguration
	{
		public ProjectCompileConfiguration(string outputPath)
		{
			OutputPath = outputPath;
		}

		public string OutputPath { get; }

		public string SymbolsPath { get; private set; }

		public string XmlDocumentationPath { get; private set; }

		IProjectCompileConfiguration IProjectCompileConfiguration.SymbolsPath(string path)
		{
			if (path == null)
			{
				throw new ArgumentNullException(nameof(path));
			}
			SymbolsPath = path;
			return this;
		}

		IProjectCompileConfiguration IProjectCompileConfiguration.XmlDocumentationPath(string path)
		{
			if (path == null)
			{
				throw new ArgumentNullException(nameof(path));
			}
			XmlDocumentationPath = path;
			return this;
		}
	}
}
