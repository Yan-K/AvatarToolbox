using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace YanK
{
	public static class DependencyCollector
	{
		public struct CollectResult
		{
			public string[] AssetPaths;
			public string[] MissingDependencies;
		}

		public static CollectResult Collect(IEnumerable<string> rootPaths, bool includePackages = false)
		{
			HashSet<string> seeds = new HashSet<string>();

			foreach (string raw in rootPaths)
			{
				if (string.IsNullOrEmpty(raw))
					continue;

				string p = raw.Replace('\\', '/');

				if (AssetDatabase.IsValidFolder(p))
				{
					string[] guids = AssetDatabase.FindAssets("", new[] { p });
					foreach (string g in guids)
					{
						string ap = AssetDatabase.GUIDToAssetPath(g);
						if (!string.IsNullOrEmpty(ap) && !AssetDatabase.IsValidFolder(ap))
							seeds.Add(ap);
					}
				}
				else
				{
					seeds.Add(p);
				}
			}

			string[] seedArray = seeds.ToArray();
			string[] deps = AssetDatabase.GetDependencies(seedArray, true);

			HashSet<string> resolved = new HashSet<string>();
			List<string> missing = new List<string>();

			foreach (string d in deps)
			{
				if (string.IsNullOrEmpty(d))
					continue;

				string dep = d.Replace('\\', '/');

				if (!includePackages && !dep.StartsWith("Assets/"))
					continue;

				string guid = AssetDatabase.AssetPathToGUID(dep);
				if (string.IsNullOrEmpty(guid))
				{
					missing.Add(dep);
					continue;
				}

				resolved.Add(dep);
			}

			string[] sortedPaths = resolved.ToArray();
			System.Array.Sort(sortedPaths, System.StringComparer.Ordinal);

			return new CollectResult
			{
				AssetPaths = sortedPaths,
				MissingDependencies = missing.ToArray()
			};
		}
	}
}
