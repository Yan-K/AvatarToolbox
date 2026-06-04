using System.Collections.Generic;
using UnityEditor;

namespace YanK
{
	// A thread-safe, read-only snapshot of the project's path<->GUID mapping.
	// Built once on the main thread (it calls AssetDatabase), then queried freely
	// from background threads while loading packages.
	public sealed class SnapshotAssetProbe : IAssetProbe
	{
		private readonly Dictionary<string, string> guidByPath;
		private readonly Dictionary<string, string> pathByGuid;

		private SnapshotAssetProbe(Dictionary<string, string> guidByPath, Dictionary<string, string> pathByGuid)
		{
			this.guidByPath = guidByPath;
			this.pathByGuid = pathByGuid;
		}

		// Must be called on the main thread.
		public static SnapshotAssetProbe Capture()
		{
			string[] allPaths = AssetDatabase.GetAllAssetPaths();
			var guidByPath = new Dictionary<string, string>(allPaths.Length);
			var pathByGuid = new Dictionary<string, string>(allPaths.Length);

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

			return new SnapshotAssetProbe(guidByPath, pathByGuid);
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
	}
}
