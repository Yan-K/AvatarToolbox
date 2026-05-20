using System.Collections.Generic;

namespace YanK
{
	public sealed class LoadedPackage
	{
		public string FilePath;
		public List<UnityPackageEntry> Entries = new List<UnityPackageEntry>();
		public PackageAssetNode Tree;
		public bool IsExpanded = true;
		public Dictionary<string, ImportConflict> ConflictByGuid = new Dictionary<string, ImportConflict>();
	}
}
