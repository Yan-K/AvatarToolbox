using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace YanK
{
	// A thread-safe, read-only snapshot of the project's path<->GUID mapping.
	// Built once on the main thread (it calls AssetDatabase), then queried freely
	// from background threads while loading packages.
	public sealed class SnapshotAssetProbe : IAssetProbe
	{
		private readonly Dictionary<string, string> guidByPath;
		private readonly Dictionary<string, string> pathByGuid;
		private readonly string projectRoot;
		private readonly bool includeContent;
		private readonly HashSet<string> contentUnknownPaths;
		private readonly object contentGate = new object();
		private readonly Dictionary<string, AssetContentFingerprint> contentByPath
			= new Dictionary<string, AssetContentFingerprint>(System.StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> contentMisses
			= new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

		private SnapshotAssetProbe(
			Dictionary<string, string> guidByPath,
			Dictionary<string, string> pathByGuid,
			string projectRoot,
			bool includeContent,
			IEnumerable<string> contentUnknownPaths)
		{
			this.guidByPath = guidByPath;
			this.pathByGuid = pathByGuid;
			this.projectRoot = projectRoot;
			this.includeContent = includeContent;
			this.contentUnknownPaths = contentUnknownPaths == null
				? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
				: new HashSet<string>(contentUnknownPaths, StringComparer.OrdinalIgnoreCase);
		}

		// Must be called on the main thread.
		public static SnapshotAssetProbe Capture(
			bool includeContent = false,
			IEnumerable<string> contentUnknownPaths = null)
		{
			string[] allPaths = AssetDatabase.GetAllAssetPaths();
			var guidByPath = new Dictionary<string, string>(allPaths.Length, System.StringComparer.OrdinalIgnoreCase);
			var pathByGuid = new Dictionary<string, string>(allPaths.Length, System.StringComparer.OrdinalIgnoreCase);

			foreach (string path in allPaths)
			{
				if (string.IsNullOrEmpty(path))
					continue;
				string guid = AssetDatabase.AssetPathToGUID(path);
				if (string.IsNullOrEmpty(guid))
					continue;
				guidByPath[path] = guid;
				// First writer wins; GUIDs are unique per asset in practice.
				if (!pathByGuid.ContainsKey(guid))
					pathByGuid[guid] = path;
			}

			string projectRoot = Directory.GetParent(Application.dataPath).FullName;
			return new SnapshotAssetProbe(
				guidByPath, pathByGuid, projectRoot, includeContent, contentUnknownPaths);
		}

		// Must be called on Unity's main thread and outside serialization/IMGUI.
		// Only already-loaded dirty assets that a dropped package entry can target are
		// saved. Clean or unrelated project assets are never loaded just for this check.
		public static bool SaveDirtyTargets(
			IEnumerable<LoadedPackage> packages,
			out HashSet<string> contentUnknownPaths)
		{
			contentUnknownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var targetGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (packages == null)
				return false;

			foreach (LoadedPackage package in packages)
			{
				if (package == null || package.Tree == null || package.Entries == null)
					continue;
				foreach (UnityPackageEntry entry in package.Entries)
				{
					if (entry == null)
						continue;

					if (!string.IsNullOrEmpty(entry.IdentityGuid)
						&& !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(entry.IdentityGuid)))
						targetGuids.Add(entry.IdentityGuid);

					if (!string.IsNullOrEmpty(entry.AssetPath))
					{
						string pathGuid = AssetDatabase.AssetPathToGUID(entry.AssetPath);
						if (!string.IsNullOrEmpty(pathGuid))
							targetGuids.Add(pathGuid);
					}
				}
			}

			bool anySaved = false;
			foreach (string guid in targetGuids)
			{
				string path = AssetDatabase.GUIDToAssetPath(guid);
				if (string.IsNullOrEmpty(path))
					continue;
				if (!AssetDatabase.IsMainAssetAtPathLoaded(path))
					continue;

				UnityEngine.Object[] objects;
				try
				{
					objects = AssetDatabase.LoadAllAssetsAtPath(path);
				}
				catch (Exception ex)
				{
					contentUnknownPaths.Add(path);
					Debug.LogWarning("[YSP] Could not inspect a targeted asset before comparison: "
						+ path + "\n" + ex);
					continue;
				}

				bool wasDirty = false;
				for (int i = 0; i < objects.Length; i++)
				{
					UnityEngine.Object asset = objects[i];
					if (asset == null || !EditorUtility.IsDirty(asset))
						continue;
					wasDirty = true;
					try
					{
						AssetDatabase.SaveAssetIfDirty(asset);
					}
					catch (Exception ex)
					{
						contentUnknownPaths.Add(path);
						Debug.LogWarning("[YSP] Could not save a dirty targeted asset before comparison: "
							+ path + "\n" + ex);
						break;
					}
				}

				if (!wasDirty)
					continue;

				bool stillDirty = false;
				for (int i = 0; i < objects.Length; i++)
				{
					if (objects[i] != null && EditorUtility.IsDirty(objects[i]))
					{
						stillDirty = true;
						break;
					}
				}
				if (stillDirty)
					contentUnknownPaths.Add(path);
				else if (!contentUnknownPaths.Contains(path))
					anySaved = true;
			}

			return anySaved;
		}

		public string GuidAt(string path)
		{
			if (string.IsNullOrEmpty(path))
				return null;
			return guidByPath.TryGetValue(path, out string guid) ? guid : null;
		}

		public string PathAt(string guid)
		{
			if (string.IsNullOrEmpty(guid))
				return null;
			return pathByGuid.TryGetValue(guid, out string path) ? path : null;
		}

		public bool ExistsAt(string path)
		{
			if (string.IsNullOrEmpty(path))
				return false;
			return ProjectPackagePath.TryGetAbsolutePath(projectRoot, path, out string absolute, out _)
				&& (File.Exists(absolute) || Directory.Exists(absolute) || File.Exists(absolute + ".meta"));
		}

		public bool IsFolderAt(string path)
		{
			if (string.IsNullOrEmpty(path))
				return false;
			return ProjectPackagePath.TryGetAbsolutePath(projectRoot, path, out string absolute, out _)
				&& Directory.Exists(absolute);
		}

		public bool TryGetContent(string path, out AssetContentFingerprint content)
		{
			content = default;
			if (!includeContent || string.IsNullOrEmpty(path) || contentUnknownPaths.Contains(path))
				return false;

			lock (contentGate)
			{
				if (contentByPath.TryGetValue(path, out content))
					return true;
				if (contentMisses.Contains(path))
					return false;
			}

			bool found = TryReadContent(projectRoot, path, out content);
			lock (contentGate)
			{
				if (found)
					contentByPath[path] = content;
				else
					contentMisses.Add(path);
			}
			return found;
		}

		public static bool TryReadContent(string projectRoot, string path, out AssetContentFingerprint content)
		{
			content = default;
			if (!ProjectPackagePath.TryGetAbsolutePath(projectRoot, path, out string absolute, out _))
				return false;

			bool isFile = File.Exists(absolute);
			bool isFolder = Directory.Exists(absolute);
			string metaPath = absolute + ".meta";
			bool hasMeta = File.Exists(metaPath);
			if (!isFile && !isFolder && !hasMeta)
				return false;

			content.HasAsset = isFile;
			content.HasMeta = hasMeta;
			if (isFile && !TryComputeStableHash(absolute, out content.AssetHash))
			{
				content = default;
				return false;
			}
			if (hasMeta && !TryComputeStableHash(metaPath, out content.MetaHash))
			{
				content = default;
				return false;
			}
			if (File.Exists(absolute) != isFile
				|| Directory.Exists(absolute) != isFolder
				|| File.Exists(metaPath) != hasMeta)
			{
				content = default;
				return false;
			}
			return true;
		}

		private static bool TryComputeStableHash(string path, out string hash)
		{
			hash = null;
			try
			{
				var before = new FileInfo(path);
				long length = before.Length;
				long lastWriteTicks = before.LastWriteTimeUtc.Ticks;
				using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
					hash = GuidUtility.ComputeSha256(stream);

				var after = new FileInfo(path);
				if (after.Length != length || after.LastWriteTimeUtc.Ticks != lastWriteTicks)
				{
					hash = null;
					return false;
				}
				return true;
			}
			catch (IOException)
			{
				hash = null;
				return false;
			}
			catch (UnauthorizedAccessException)
			{
				hash = null;
				return false;
			}
		}
	}
}
