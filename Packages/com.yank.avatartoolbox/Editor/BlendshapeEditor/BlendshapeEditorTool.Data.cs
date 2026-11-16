namespace YanK
{
	public partial class BlendshapeEditorTool
	{
		private class BlendshapeSlot
		{
			public int index;           // Index on the SkinnedMeshRenderer.sharedMesh
			public string name;
			public float current;       // 0-100
			public float defaultValue;  // Session original (see RescanSlots); used by Reset to Default
			public bool selected;       // Row checkbox
			public bool isSeparator;    // Detected group-separator entry (not shown in main list)
			public string groupName;    // Assigned group ("General" before any separator)
		}

		private enum ExportMode
		{
			AllBlendshapes,
			NonZero,
			Modified,
			CustomSelected,
		}

		private enum ImportMode
		{
			Overlay,          // Keep existing, only overwrite blendshapes present in the clip
			ResetZero,        // Reset all to 0, then apply clip
			ResetDefault,     // Reset to scan-time default, then apply clip
			Custom,           // User picks which source blendshapes to import
		}

		private class RemapEntry
		{
			public string sourceName;      // Blendshape name from the animation clip
			public int targetSlotIndex;    // -1 = ignore; otherwise index into `slots`
			public float sourceValue;      // Cached sampled value (for display)
		}

		private const string GroupViewAllKey = "__VIEW_ALL__";
		private const string GroupGeneralKey = "__GENERAL__";

		// Weight difference from the original that counts as an edit (yellow row).
		private const float ModifiedThreshold = 0.01f;
	}
}
