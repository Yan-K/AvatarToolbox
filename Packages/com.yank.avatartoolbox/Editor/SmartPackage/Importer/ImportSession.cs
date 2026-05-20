using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace YanK
{
	public static class ImportSession
	{
		public static void Apply(IEnumerable<LoadedPackage> packages, ConflictPolicy policy)
		{
			if (packages == null)
				return;

			List<(LoadedPackage pkg, UnityPackageEntry entry)> queue = new List<(LoadedPackage, UnityPackageEntry)>();

			foreach (LoadedPackage pkg in packages)
			{
				if (pkg == null || pkg.Tree == null)
					continue;

				Dictionary<string, UnityPackageEntry> entryByPath = new Dictionary<string, UnityPackageEntry>();
				foreach (UnityPackageEntry e in pkg.Entries)
				{
					if (e != null && !string.IsNullOrEmpty(e.AssetPath))
						entryByPath[e.AssetPath] = e;
				}

				foreach (PackageAssetNode leaf in pkg.Tree.EnumerateCheckedLeaves())
				{
					if (entryByPath.TryGetValue(leaf.FullPath, out UnityPackageEntry entry))
						queue.Add((pkg, entry));
				}
			}

			if (queue.Count == 0)
			{
				EditorUtility.DisplayDialog("Smart Package Import", "Nothing selected.", "OK");
				return;
			}

			string projectRoot = Directory.GetParent(Application.dataPath).FullName;

			bool skipAll = false;
			int written = 0;
			int skipped = 0;
			int failed = 0;
			bool cancelled = false;

			try
			{
				for (int i = 0; i < queue.Count; i++)
				{
					var (pkg, entry) = queue[i];

					if (EditorUtility.DisplayCancelableProgressBar(
						"Importing…",
						string.Format("{0} / {1}  {2}", i + 1, queue.Count, entry.AssetPath),
						(float)(i + 1) / queue.Count))
					{
						cancelled = true;
						break;
					}

					ImportConflictKind kind = ImportConflictKind.New;
					if (pkg.ConflictByGuid != null && pkg.ConflictByGuid.TryGetValue(entry.Guid, out ImportConflict c))
						kind = c.Kind;

					bool overwrite = true;
					if (kind != ImportConflictKind.New)
					{
						if (policy == ConflictPolicy.Skip)
						{
							skipped++;
							continue;
						}
						if (policy == ConflictPolicy.Ask)
						{
							if (skipAll)
							{
								skipped++;
								continue;
							}
							int choice = EditorUtility.DisplayDialogComplex(
								"Conflict: " + entry.AssetPath,
								"Kind: " + kind + "\nExisting: " + (string.IsNullOrEmpty(GetExisting(pkg, entry)) ? "(none)" : GetExisting(pkg, entry)),
								"Overwrite", "Skip", "Skip All");
							if (choice == 1)
							{
								skipped++;
								continue;
							}
							if (choice == 2)
							{
								skipAll = true;
								skipped++;
								continue;
							}
							overwrite = true;
						}
					}

					if (!overwrite)
					{
						skipped++;
						continue;
					}

					string absPath = Path.Combine(projectRoot, entry.AssetPath.Replace('/', Path.DirectorySeparatorChar));
					string absMeta = absPath + ".meta";

					string parent = Path.GetDirectoryName(absPath);
					if (!string.IsNullOrEmpty(parent))
						Directory.CreateDirectory(parent);

					if (entry.AssetBytes != null)
						File.WriteAllBytes(absPath, entry.AssetBytes);
					else if (!Directory.Exists(absPath))
						Directory.CreateDirectory(absPath);

					if (entry.MetaBytes != null)
						File.WriteAllBytes(absMeta, entry.MetaBytes);

					written++;
				}
			}
			catch (System.Exception ex)
			{
				failed++;
				Debug.LogError("[YSP] Import error: " + ex);
			}
			finally
			{
				EditorUtility.ClearProgressBar();
			}

			AssetDatabase.Refresh(ImportAssetOptions.Default);

			string summary = string.Format("Written: {0}\nSkipped: {1}\nFailed: {2}{3}",
				written, skipped, failed, cancelled ? "\n(Cancelled)" : "");
			EditorUtility.DisplayDialog("Smart Package Import", summary, "OK");
		}

		private static string GetExisting(LoadedPackage pkg, UnityPackageEntry entry)
		{
			if (pkg.ConflictByGuid != null && pkg.ConflictByGuid.TryGetValue(entry.Guid, out ImportConflict c))
				return c.ExistingPath;
			return null;
		}
	}
}
