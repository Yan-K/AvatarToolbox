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

		// Conflict lookup keyed by asset path, built once on load so the UI never
		// rebuilds it per repaint.
		public Dictionary<string, ImportConflict> ConflictByPath = new Dictionary<string, ImportConflict>();

		// Tallies reflecting only CHECKED leaves; recomputed lazily when the checked
		// state changes (tracked via TallyVersion) rather than every frame.
		public int GuidConflictCount;
		public int PathConflictCount;
		public int UpdateCount;
		public int SelectedCount;
		public int TallyVersion = -1;
	}
}
