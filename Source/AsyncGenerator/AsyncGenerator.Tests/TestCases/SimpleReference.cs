﻿using System.IO;
using AsyncGenerator.TestCases;

namespace AsyncGenerator.Tests.TestCases
{
	/// <summary>
	/// A simple example of using <see cref="SimpleFile.Read"/>
	/// </summary>
	public class SimpleReference
	{
		public void CallCallReadFile()
		{
			CallReadFile();
		}

		public void CallReadFile()
		{
			ReadFile();
		}

		/// <summary>
		/// Use <see cref="SimpleFile.Read"/> to read a file
		/// </summary>
		public void ReadFile()
		{
			SimpleFile.Read();
		}

	}
}
