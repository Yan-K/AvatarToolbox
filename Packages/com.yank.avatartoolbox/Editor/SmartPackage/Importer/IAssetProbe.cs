using UnityEditor;

namespace YanK
{
	public struct AssetContentFingerprint
	{
		public bool HasAsset;
		public bool HasMeta;
		public string AssetHash;
		public string MetaHash;
	}

	public interface IAssetProbe
	{
		string GuidAt(string path);
		string PathAt(string guid);
		bool ExistsAt(string path);
		bool IsFolderAt(string path);
		bool TryGetContent(string path, out AssetContentFingerprint content);
	}

	public sealed class AssetDatabaseProbe : IAssetProbe
	{
		public string GuidAt(string path) => AssetDatabase.AssetPathToGUID(path);
		public string PathAt(string guid) => AssetDatabase.GUIDToAssetPath(guid);
		public bool ExistsAt(string path)
		{
			string projectRoot = System.IO.Directory.GetParent(UnityEngine.Application.dataPath).FullName;
			return ProjectPackagePath.TryGetAbsolutePath(projectRoot, path, out string absolute, out _)
				&& (System.IO.File.Exists(absolute) || System.IO.Directory.Exists(absolute)
					|| System.IO.File.Exists(absolute + ".meta"));
		}
		public bool IsFolderAt(string path)
		{
			string projectRoot = System.IO.Directory.GetParent(UnityEngine.Application.dataPath).FullName;
			return ProjectPackagePath.TryGetAbsolutePath(projectRoot, path, out string absolute, out _)
				&& System.IO.Directory.Exists(absolute);
		}
		public bool TryGetContent(string path, out AssetContentFingerprint content)
		{
			string projectRoot = System.IO.Directory.GetParent(UnityEngine.Application.dataPath).FullName;
			return SnapshotAssetProbe.TryReadContent(projectRoot, path, out content);
		}
	}
}
