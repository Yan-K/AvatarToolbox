using System.Text;

namespace YanK
{
	public static class GuidUtility
	{
		static readonly char[] TrimChars = new[] { '\uFEFF', '\r', '\n', '\0', ' ', '\t' };

		public static string ExtractGuidFromMeta(byte[] meta)
		{
			if (meta == null || meta.Length == 0) return null;
			string text = Encoding.UTF8.GetString(meta);
			string[] lines = text.Split('\n');
			for (int i = 0; i < lines.Length; i++)
			{
				string line = lines[i].Trim(TrimChars);
				if (line.StartsWith("guid:"))
				{
					string value = line.Substring(5).Trim(TrimChars);
					if (IsValidGuid(value)) return value;
				}
			}
			return null;
		}

		public static string NormalizePathname(byte[] bytes)
		{
			if (bytes == null || bytes.Length == 0) return string.Empty;
			string text = Encoding.UTF8.GetString(bytes);
			string[] lines = text.Split('\n');
			for (int i = 0; i < lines.Length; i++)
			{
				string line = lines[i].Trim(TrimChars);
				if (line.Length > 0) return line;
			}
			return string.Empty;
		}

		public static bool IsValidGuid(string s)
		{
			if (string.IsNullOrEmpty(s) || s.Length != 32) return false;
			for (int i = 0; i < s.Length; i++)
			{
				char c = s[i];
				bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
				if (!ok) return false;
			}
			return true;
		}
	}
}
