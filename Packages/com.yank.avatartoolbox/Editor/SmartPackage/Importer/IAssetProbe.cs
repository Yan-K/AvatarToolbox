using UnityEditor;

namespace YanK
{
	public interface IAssetProbe
	{
		string GuidAt(string path);
		string PathAt(string guid);
	}

	public sealed class AssetDatabaseProbe : IAssetProbe
	{
		public string GuidAt(string path) => AssetDatabase.AssetPathToGUID(path);
		public string PathAt(string guid) => AssetDatabase.GUIDToAssetPath(guid);
	}
}
