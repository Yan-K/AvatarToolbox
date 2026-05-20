using System.Collections.Generic;

namespace YanK
{
	public class AssetTypeFilter
	{
		public Dictionary<string, int> Counts = new Dictionary<string, int>();
		public Dictionary<string, bool> Visibility = new Dictionary<string, bool>();

		public void Rebuild(IEnumerable<PackageAssetNode> leaves)
		{
			Counts.Clear();

			foreach (PackageAssetNode n in leaves)
			{
				if (n == null || n.IsFolder)
					continue;

				string ext = string.IsNullOrEmpty(n.Extension) ? "" : n.Extension.ToLowerInvariant();
				if (!Counts.ContainsKey(ext))
					Counts[ext] = 0;
				Counts[ext]++;

				if (!Visibility.ContainsKey(ext))
					Visibility[ext] = true;
			}

			List<string> stale = new List<string>();
			foreach (KeyValuePair<string, bool> kv in Visibility)
			{
				if (!Counts.ContainsKey(kv.Key))
					stale.Add(kv.Key);
			}
			foreach (string k in stale)
				Visibility.Remove(k);
		}

		public bool IsVisible(PackageAssetNode node)
		{
			if (node == null || node.IsFolder)
				return true;

			string ext = string.IsNullOrEmpty(node.Extension) ? "" : node.Extension.ToLowerInvariant();
			if (Visibility.TryGetValue(ext, out bool v))
				return v;
			return true;
		}
	}
}
