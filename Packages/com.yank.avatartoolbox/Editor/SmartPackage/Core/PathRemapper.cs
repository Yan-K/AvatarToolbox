using System;
using System.Collections.Generic;
using System.IO;

namespace YanK
{
	public sealed class PathRemapper
	{
		private static readonly Dictionary<string, string> BucketByExtension = BuildBucketTable();

		private readonly FolderCollectionMode mode;
		private readonly string rootFolderName;
		private readonly HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		public PathRemapper(FolderCollectionMode mode, string rootFolderName)
		{
			this.mode = mode;
			this.rootFolderName = string.IsNullOrEmpty(rootFolderName) ? "YSP_Export" : rootFolderName;
		}

		public string Remap(string originalAssetPath)
		{
			if (string.IsNullOrEmpty(originalAssetPath))
				return originalAssetPath;

			string normalized = originalAssetPath.Replace('\\', '/');

			switch (mode)
			{
				case FolderCollectionMode.KeepStructure:
					return Reserve(BuildKeepStructure(normalized), reserve: false);
				case FolderCollectionMode.AutoOrganize:
					return Reserve(BuildAutoOrganize(normalized), reserve: true);
				case FolderCollectionMode.SingleFolder:
					return Reserve(BuildSingleFolder(normalized), reserve: true);
				default:
					return Reserve(BuildKeepStructure(normalized), reserve: false);
			}
		}

		private string BuildKeepStructure(string normalized)
		{
			string relative = StripAssetsPrefix(normalized);
			if (string.IsNullOrEmpty(relative))
				relative = Path.GetFileName(normalized);
			return "Assets/" + rootFolderName + "/" + relative;
		}

		private string BuildAutoOrganize(string normalized)
		{
			string fileName = Path.GetFileName(normalized);
			string ext = Path.GetExtension(fileName).ToLowerInvariant();
			string bucket = BucketByExtension.TryGetValue(ext, out string b) ? b : "Other";
			return "Assets/" + rootFolderName + "/" + bucket + "/" + fileName;
		}

		private string BuildSingleFolder(string normalized)
		{
			string fileName = Path.GetFileName(normalized);
			return "Assets/" + rootFolderName + "/" + fileName;
		}

		private string Reserve(string candidate, bool reserve)
		{
			if (!reserve)
			{
				taken.Add(candidate);
				return candidate;
			}

			if (taken.Add(candidate))
				return candidate;

			string dir = Path.GetDirectoryName(candidate).Replace('\\', '/');
			string stem = Path.GetFileNameWithoutExtension(candidate);
			string ext = Path.GetExtension(candidate);
			int n = 1;
			while (true)
			{
				string next = (string.IsNullOrEmpty(dir) ? "" : dir + "/") + stem + "_" + n + ext;
				if (taken.Add(next))
					return next;
				n++;
			}
		}

		private static string StripAssetsPrefix(string normalized)
		{
			if (normalized.StartsWith("Assets/", StringComparison.Ordinal))
				return normalized.Substring("Assets/".Length);
			if (normalized.Equals("Assets", StringComparison.Ordinal))
				return string.Empty;
			return normalized;
		}

		private static Dictionary<string, string> BuildBucketTable()
		{
			Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			Add(map, "Models", ".fbx", ".obj", ".blend", ".dae", ".3ds");
			Add(map, "Materials", ".mat");
			Add(map, "Textures", ".png", ".jpg", ".jpeg", ".tga", ".psd", ".exr", ".hdr", ".tif", ".tiff", ".bmp");
			Add(map, "Animations", ".anim", ".controller", ".overrideController");
			Add(map, "Shaders", ".shader", ".shadergraph", ".compute", ".cginc", ".hlsl");
			Add(map, "Prefabs", ".prefab");
			Add(map, "Scenes", ".unity");
			Add(map, "Audio", ".wav", ".mp3", ".ogg", ".aif", ".aiff", ".flac");
			Add(map, "Scripts", ".cs", ".asmdef", ".asmref");
			return map;
		}

		private static void Add(Dictionary<string, string> map, string bucket, params string[] exts)
		{
			foreach (string e in exts)
				map[e] = bucket;
		}
	}
}
