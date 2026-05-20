using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace YanK
{
	public class RegexExcluder
	{
		private readonly List<Regex> regexes = new List<Regex>();

		public RegexExcluder(IEnumerable<string> patterns)
		{
			if (patterns == null)
				return;

			foreach (string raw in patterns)
			{
				if (string.IsNullOrWhiteSpace(raw))
					continue;

				string pattern = raw.Trim();
				try
				{
					regexes.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled));
				}
				catch (System.ArgumentException ex)
				{
					Debug.LogWarning("[YSP] Invalid regex pattern skipped: \"" + pattern + "\" — " + ex.Message);
				}
			}
		}

		public bool IsExcluded(string path)
		{
			if (string.IsNullOrEmpty(path))
				return false;

			foreach (Regex r in regexes)
			{
				if (r.IsMatch(path))
					return true;
			}
			return false;
		}

		public int CountExcluded(IEnumerable<string> paths)
		{
			if (paths == null) return 0;
			int n = 0;
			foreach (string p in paths)
			{
				if (IsExcluded(p)) n++;
			}
			return n;
		}
	}
}
