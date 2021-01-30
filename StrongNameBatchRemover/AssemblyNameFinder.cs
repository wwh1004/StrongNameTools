using System;
using System.Collections.Generic;
using System.Text;
using dnlib.DotNet;

namespace StrongNameBatchRemover {
	internal static class AssemblyNameFinder {
		private static readonly byte[] VersionBytes = Encoding.UTF8.GetBytes("Version");
		private static readonly byte[] CultureBytes = Encoding.UTF8.GetBytes("Culture");
		private static readonly byte[] PublicKeyTokenBytes = Encoding.UTF8.GetBytes("PublicKeyToken");

		public static int[] FindAll(ReadOnlySpan<byte> data, IAssembly assembly) {
			var offsets = new List<int>();
			var name = assembly.Name.Data.AsSpan();
			for (int i = 0; i < data.Length; i++) {
				int offset = i;
				if (!IsToken(data, ref offset, name) || !(data[offset] == ' ' || data[offset] == ',') || !NextTokenValue(data, ref offset, VersionBytes) || !NextTokenValue(data, ref offset, CultureBytes) || !NextTokenValue(data, ref offset, PublicKeyTokenBytes))
					continue;

				if ((data.Length - offset) < 4)
					break;

				if (string.Equals(Encoding.UTF8.GetString(data.Slice(offset, 4)), "null", StringComparison.OrdinalIgnoreCase))
					continue;

				{
					string s = Encoding.UTF8.GetString(data.Slice(i, offset - i + 16));
					var assemblyName = new AssemblyNameInfo(s);
					if (assemblyName.Name != assembly.Name || assemblyName.PublicKeyOrToken.IsNullOrEmpty)
						throw new InvalidOperationException();
					// TODO: 严格检测
				}

				offsets.Add(offset);
			}
			return offsets.ToArray();
		}

		static bool NextTokenValue(ReadOnlySpan<byte> data, ref int offset, ReadOnlySpan<byte> token, bool caseInsensitive = false) {
			return NextToken(data, ref offset) && IsToken(data, ref offset, token, caseInsensitive) && NextChar(data, ref offset, '=') && SkipWhiteSpace(data, ref offset);
		}

		static bool IsToken(ReadOnlySpan<byte> data, ref int offset, ReadOnlySpan<byte> token, bool caseInsensitive = false) {
			if (data.IsEmpty || (data.Length - offset) < token.Length)
				return false;

			if (!caseInsensitive) {
				for (int i = 0; i < token.Length; i++) {
					byte x = data[offset + i];
					if ('A' <= x && x <= 'Z')
						x -= 'a' - 'A';
					byte y = token[i];
					if ('A' <= y && y <= 'Z')
						y -= 'a' - 'A';
					if (x != y) {
						offset += i;
						return false;
					}
				}
				offset += token.Length;
				return true;
			}
			else {
				bool result = data.SequenceEqual(token);
				offset += token.Length;
				return result;
			}
		}

		static bool NextToken(ReadOnlySpan<byte> data, ref int offset) {
			return !data.IsEmpty && NextChar(data, ref offset, ',') && SkipWhiteSpace(data, ref offset);
		}

		static bool SkipWhiteSpace(ReadOnlySpan<byte> data, ref int offset) {
			if (data.IsEmpty || offset >= data.Length)
				return false;

			int i = 0;
			for (; i < data.Length - offset; i++) {
				if (data[offset + i] != ' ')
					break;
			}
			offset += i;
			return !data.IsEmpty;
		}

		static bool NextChar(ReadOnlySpan<byte> data, ref int offset, char chr) {
			if (data.IsEmpty || offset >= data.Length)
				return false;

			int i = 0;
			for (; i < data.Length - offset; i++) {
				if (data[offset + i] == chr) {
					offset += i + 1;
					return true;
				}
			}
			return false;
		}
	}
}
