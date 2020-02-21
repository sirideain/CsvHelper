﻿#if NETSTANDARD2_1
using CsvHelper.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace CsvHelper
{
	public class CsvStackParser : IDisposable
	{
		private TextReader reader;
		private int bufferPosition;
		private Memory<char> heapBuffer = new Memory<char>();
		private int charsRead;
		private int c = -1;
		private char escape = '"';
		private string delimiter = ",";
		private int delimiterFirstChar = ',';
		private string newLine = string.Empty;
		private int newLineFirstChar = -2;
		private bool leaveOpen;
		private CsvConfiguration configuration;
		private BufferPosition currentFieldPosition;
		private List<BufferPosition> currentFieldPositions;
		//private BufferPosition rawFieldPosition = new BufferPosition();
		private BufferPosition rawRecordPosition = new BufferPosition();
		private List<List<BufferPosition>> fieldPositions = new List<List<BufferPosition>>(128);
		//private List<BufferPosition> rawFieldPositions = new List<BufferPosition>();

		public CsvConfiguration Configuration => configuration;

		public int Row { get; protected set; }

		public int RawRow { get; protected set; }

		public string RawRecord => new string(heapBuffer.Span.Slice(rawRecordPosition.Start, rawRecordPosition.Length));

		public string this[int index]
		{
			get
			{
				var positions = fieldPositions[index];
				Span<char> fieldBuffer = stackalloc char[positions.Sum(p => p.Length)];
				var start = 0;
				for (var i = 0; i < positions.Count; i++)
				{
					heapBuffer.Span.Slice(positions[i].Start, positions[i].Length).CopyTo(fieldBuffer.Slice(start));
					start += positions[i].Length;
				}

				return new string(fieldBuffer);
				//return new string(heapBuffer.Span.Slice(positions.Start, positions.Length));
			}
		}

		public CsvStackParser(TextReader reader, CultureInfo culture) : this(reader, culture, false) { }

		public CsvStackParser(TextReader reader, CultureInfo culture, bool leaveOpen) : this(reader, new CsvConfiguration(culture)) { }

		public CsvStackParser(TextReader reader, CsvConfiguration configuration) : this(reader, configuration, false) { }

		public CsvStackParser(TextReader reader, CsvConfiguration configuration, bool leaveOpen)
		{
			this.reader = reader;
			this.configuration = configuration;
			this.leaveOpen = leaveOpen;

			currentFieldPosition = new BufferPosition();
			currentFieldPositions = new List<BufferPosition> { currentFieldPosition };
		}

		public bool Read()
		{
			Row++;
			RawRow++;
			fieldPositions.Clear();
			rawRecordPosition.Start = bufferPosition;
			rawRecordPosition.Length = 0;

			var stackBuffer = heapBuffer.Span.Slice(0);

			while (true)
			{
				c = GetChar(ref stackBuffer);
				if (c == -1)
				{
					// End of file.
					return false;
				}

				if (c == escape)
				{
					ReadQuotedField(ref stackBuffer);
				}
				else
				{
					if (ReadField(ref stackBuffer))
					{
						break;
					}
				}
			}

			return true;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		protected bool ReadField(ref Span<char> stackBuffer)
		{
			while (true)
			{
				if (c == delimiterFirstChar)
				{
					if (ReadDelimiter(ref stackBuffer))
					{
						return false;
					}
				}
				else if (c == newLineFirstChar || c == '\r' || c == '\n')
				{
					if (ReadLineEnding(ref stackBuffer))
					{
						return true;
					}
				}

				c = GetChar(ref stackBuffer);
			}
		}

		protected bool ReadQuotedField(ref Span<char> stackBuffer)
		{
			while (true)
			{
				if (c == delimiterFirstChar)
				{
					if (ReadDelimiter(ref stackBuffer))
					{
						return false;
					}
				}
				else if (c == newLineFirstChar || c == '\r' || c == '\n')
				{
					if (ReadLineEnding(ref stackBuffer))
					{
						return true;
					}
				}

				c = GetChar(ref stackBuffer);
			}
		}

		protected bool ReadDelimiter(ref Span<char> stackBuffer)
		{
			Debug.Assert(c == delimiter[0], "Tried reading a delimiter when the first delimiter char didn't match the current char.");

			if (delimiter.Length > 1)
			{
				for (var i = 1; i < delimiter.Length; i++)
				{
					c = GetChar(ref stackBuffer);
					if (c != delimiter[i])
					{
						return false;
					}
				}
			}

			currentFieldPosition.Length -= delimiter.Length;

			fieldPositions.Add(currentFieldPositions);
			currentFieldPosition = new BufferPosition { Start = bufferPosition };
			currentFieldPositions = new List<BufferPosition> { currentFieldPosition };

			//rawFieldPosition.Length -= delimiter.Length;
			//rawFieldPositions.Add(rawFieldPosition);
			//rawFieldPosition = new BufferPosition
			//{
			//	Start = bufferPosition,
			//};

			return true;
		}

		protected bool ReadLineEnding(ref Span<char> stackBuffer)
		{
			Debug.Assert((newLine.Length > 0 && c != newLine[0]) || c != '\r' || c != '\n', "Tried reading a line ending when the first delimiter char didn't match the current char and wasn't \\r or \\n.");

			if (newLine.Length > 0 && c == newLine[0])
			{
				if (newLine.Length == 1)
				{
					return true;
				}

				for (var i = 1; i < newLine.Length; i++)
				{

					c = GetChar(ref stackBuffer);
					if (c != newLine[i])
					{
						return false;
					}

					return true;
				}

				currentFieldPosition.Length -= newLine.Length;
				//rawFieldPosition.Length -= newLine.Length;
			}
			else if (c == '\r')
			{
				currentFieldPosition.Length--;
				//rawFieldPosition.Length--;

				if (PeekChar(ref stackBuffer) == '\n')
				{
					c = GetChar(ref stackBuffer);
					currentFieldPosition.Length--;
					//rawFieldPosition.Length--;
				}
			}
			else // \n
			{
				currentFieldPosition.Length--;
				//rawFieldPosition.Length--;
			}

			fieldPositions.Add(currentFieldPositions);
			currentFieldPosition = new BufferPosition { Start = bufferPosition };
			currentFieldPositions = new List<BufferPosition> { currentFieldPosition };

			//rawFieldPositions.Add(rawFieldPosition);
			//rawFieldPosition = new BufferPosition
			//{
			//	Start = bufferPosition,
			//};

			return true;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		protected int GetChar(ref Span<char> stackBuffer)
		{
			if (!FillBuffer(ref stackBuffer))
			{
				return -1;
			}

			var c = stackBuffer[bufferPosition];
			bufferPosition++;
			currentFieldPosition.Length++;
			//rawFieldPosition.Length++;
			rawRecordPosition.Length++;

			return c;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		protected int PeekChar(ref Span<char> stackBuffer)
		{
			if (bufferPosition < charsRead)
			{
				return stackBuffer[bufferPosition];
			}

			return reader.Peek();
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		protected bool FillBuffer(ref Span<char> stackBuffer)
		{
			// The buffer doesn't need to be filled yet.
			if (bufferPosition < charsRead)
			{
				return true;
			}

			var charsUsed = rawRecordPosition.Start;
			var bufferLeft = charsRead - charsUsed;
			var bufferUsed = charsRead - bufferLeft;

			var tempBuffer = new char[bufferLeft + configuration.BufferSize];
			stackBuffer.Slice(charsUsed).CopyTo(tempBuffer);
			charsRead = reader.Read(tempBuffer, bufferLeft, configuration.BufferSize);
			if (charsRead == 0)
			{
				return false;
			}

			charsRead += bufferLeft;

			heapBuffer = new Memory<char>(tempBuffer);
			stackBuffer = heapBuffer.Span.Slice(0);

			bufferPosition = bufferPosition - bufferUsed;
			currentFieldPosition.Start = currentFieldPosition.Start - bufferUsed;
			//rawFieldPosition.Start = rawFieldPosition.Start - bufferUsed;
			rawRecordPosition.Start = rawRecordPosition.Start - bufferUsed;

			return true;
		}

		public void Dispose()
		{
		}
	}
}
#endif
