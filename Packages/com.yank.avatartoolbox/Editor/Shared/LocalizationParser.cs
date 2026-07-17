using System;
using System.Collections.Generic;
using System.Text;

namespace YanK
{
	public static class LocalizationParser
	{
		public static Dictionary<string, string> Parse(string json)
		{
			if (json == null) throw new ArgumentNullException(nameof(json));

			var strings = new Dictionary<string, string>();
			int index = 0;
			SkipWhitespace(json, ref index);
			Expect(json, ref index, '{');
			SkipWhitespace(json, ref index);

			if (TryConsume(json, ref index, '}')) return strings;

			while (true)
			{
				string key = ReadString(json, ref index);
				SkipWhitespace(json, ref index);
				Expect(json, ref index, ':');
				SkipWhitespace(json, ref index);
				string value = ReadString(json, ref index);

				if (string.IsNullOrEmpty(key))
					throw Error(index, "Localization keys cannot be empty.");
				if (strings.ContainsKey(key))
					throw Error(index, $"Duplicate localization key '{key}'.");

				strings.Add(key, value);
				SkipWhitespace(json, ref index);

				if (TryConsume(json, ref index, '}')) break;
				Expect(json, ref index, ',');
				SkipWhitespace(json, ref index);
			}

			SkipWhitespace(json, ref index);
			if (index != json.Length)
				throw Error(index, "Unexpected content after the localization object.");

			return strings;
		}

		private static string ReadString(string json, ref int index)
		{
			Expect(json, ref index, '"');
			var value = new StringBuilder();

			while (index < json.Length)
			{
				char c = json[index++];
				if (c == '"') return value.ToString();
				if (c != '\\')
				{
					value.Append(c);
					continue;
				}

				if (index >= json.Length)
					throw Error(index, "Unterminated escape sequence.");

				switch (json[index++])
				{
					case '"': value.Append('"'); break;
					case '\\': value.Append('\\'); break;
					case '/': value.Append('/'); break;
					case 'b': value.Append('\b'); break;
					case 'f': value.Append('\f'); break;
					case 'n': value.Append('\n'); break;
					case 'r': value.Append('\r'); break;
					case 't': value.Append('\t'); break;
					case 'u': value.Append(ReadUnicodeEscape(json, ref index)); break;
					default: throw Error(index - 1, "Unsupported escape sequence.");
				}
			}

			throw Error(index, "Unterminated JSON string.");
		}

		private static char ReadUnicodeEscape(string json, ref int index)
		{
			if (index + 4 > json.Length)
				throw Error(index, "Incomplete Unicode escape sequence.");

			int codePoint = 0;
			for (int i = 0; i < 4; i++)
			{
				char c = json[index++];
				int digit = c >= '0' && c <= '9' ? c - '0'
					: c >= 'a' && c <= 'f' ? c - 'a' + 10
					: c >= 'A' && c <= 'F' ? c - 'A' + 10
					: -1;

				if (digit < 0) throw Error(index - 1, "Invalid Unicode escape sequence.");
				codePoint = (codePoint << 4) | digit;
			}

			return (char)codePoint;
		}

		private static void SkipWhitespace(string json, ref int index)
		{
			while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
		}

		private static bool TryConsume(string json, ref int index, char expected)
		{
			if (index >= json.Length || json[index] != expected) return false;
			index++;
			return true;
		}

		private static void Expect(string json, ref int index, char expected)
		{
			if (!TryConsume(json, ref index, expected))
				throw Error(index, $"Expected '{expected}'.");
		}

		private static FormatException Error(int index, string message)
		{
			return new FormatException($"{message} (character {index})");
		}
	}
}
