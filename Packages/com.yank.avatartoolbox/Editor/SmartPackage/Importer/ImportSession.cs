using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace YanK
{
	public static class ImportSession
	{
		// Returns true when the import ran to completion (even if some entries were
		// skipped or failed); returns false only when the user cancelled.
		public static bool Apply(IEnumerable<LoadedPackage> packages, ConflictPolicy policy)
		{
			if (packages == null)
				return false;

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
				return true;
			}

			// Load the heavy asset/meta payloads only for the selected entries, one
			// re-read per package, so dropping 100+ packages never bloats memory.
			Dictionary<LoadedPackage, Dictionary<string, LoadedAssetBytes>> bytesByPackage =
				new Dictionary<LoadedPackage, Dictionary<string, LoadedAssetBytes>>();
			{
				Dictionary<LoadedPackage, HashSet<string>> guidsByPackage = new Dictionary<LoadedPackage, HashSet<string>>();
				foreach (var (pkg, entry) in queue)
				{
					if (!guidsByPackage.TryGetValue(pkg, out HashSet<string> set))
					{
						set = new HashSet<string>();
						guidsByPackage[pkg] = set;
					}
					set.Add(entry.Guid);
				}

				try
				{
					foreach (var kv in guidsByPackage)
						bytesByPackage[kv.Key] = UnityPackageReader.ReadBytesFor(kv.Key.FilePath, kv.Value);
				}
				catch (System.Exception ex)
				{
					Debug.LogError("[YSP] Failed to read package payload: " + ex);
					EditorUtility.DisplayDialog("Smart Package Import",
						"Failed to read package payload:\n" + ex.Message, "OK");
					return true;
				}
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

					byte[] assetBytes = null;
					byte[] metaBytes = null;
					if (bytesByPackage.TryGetValue(pkg, out Dictionary<string, LoadedAssetBytes> pkgBytes)
						&& pkgBytes.TryGetValue(entry.Guid, out LoadedAssetBytes payload))
					{
						assetBytes = payload.AssetBytes;
						metaBytes = payload.MetaBytes;
					}

					string absPath = Path.Combine(projectRoot, entry.AssetPath.Replace('/', Path.DirectorySeparatorChar));
					string absMeta = absPath + ".meta";

					string parent = Path.GetDirectoryName(absPath);
					if (!string.IsNullOrEmpty(parent))
						Directory.CreateDirectory(parent);

					if (assetBytes != null)
						File.WriteAllBytes(absPath, assetBytes);
					else if (!Directory.Exists(absPath))
						Directory.CreateDirectory(absPath);

					if (metaBytes != null)
						File.WriteAllBytes(absMeta, metaBytes);

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

			return !cancelled;
		}

		private static string GetExisting(LoadedPackage pkg, UnityPackageEntry entry)
		{
			if (pkg.ConflictByGuid != null && pkg.ConflictByGuid.TryGetValue(entry.Guid, out ImportConflict c))
				return c.ExistingPath;
			return null;
		}
	}
}
