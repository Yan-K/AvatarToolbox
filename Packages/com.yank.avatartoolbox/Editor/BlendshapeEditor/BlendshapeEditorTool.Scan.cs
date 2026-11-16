using UnityEngine;
using System.Collections.Generic;

namespace YanK
{
	public partial class BlendshapeEditorTool
	{
		// Separator characters considered valid for a "group header" run.
		private static readonly char[] SeparatorChars = { '-', '_', '=', '*', '#', '+', '~', '.', '|' };

		// Session originals: the weights of targetRenderer when it was assigned, by blendshape name.
		// They survive undo/redo, rescans and script reloads, and reset only when the target renderer
		// changes (after the unsaved-edits prompt) or the window closes.
		[SerializeField] private SkinnedMeshRenderer originalsRenderer;
		[SerializeField] private List<string> originalNames = new List<string>();
		[SerializeField] private List<float> originalWeights = new List<float>();

		private void ClearOriginals()
		{
			originalsRenderer = null;
			originalNames.Clear();
			originalWeights.Clear();
		}

		private void RescanSlots()
		{
			slots.Clear();
			groupNames.Clear();
			customImportNames.Clear();
			scannedMesh = null;

			if (targetRenderer == null || targetRenderer.sharedMesh == null) return;

			var mesh = targetRenderer.sharedMesh;
			scannedMesh = mesh;

			if (originalsRenderer != targetRenderer)
			{
				ClearOriginals();
				originalsRenderer = targetRenderer;
			}
			var originals = new Dictionary<string, float>();
			for (int i = 0; i < originalNames.Count && i < originalWeights.Count; i++)
				originals[originalNames[i]] = originalWeights[i];

			string currentGroup = GroupGeneralKey;
			var seenGroups = new List<string> { GroupGeneralKey };

			for (int i = 0; i < mesh.blendShapeCount; i++)
			{
				string name = mesh.GetBlendShapeName(i);
				float weight = targetRenderer.GetBlendShapeWeight(i);
				if (!originals.TryGetValue(name, out float original))
				{
					original = weight;
					originals[name] = weight;
					originalNames.Add(name);
					originalWeights.Add(weight);
				}

				bool isSep = TryDetectGroupSeparator(name, out string groupFromSep);
				if (isSep)
				{
					currentGroup = groupFromSep;
					if (!seenGroups.Contains(currentGroup)) seenGroups.Add(currentGroup);
				}

				slots.Add(new BlendshapeSlot
				{
					index = i,
					name = name,
					current = weight,
					defaultValue = original,
					selected = false,
					isSeparator = isSep,
					groupName = currentGroup,
				});
			}

			// Only keep groups that have at least one non-separator entry
			var groupsWithContent = new HashSet<string>();
			foreach (var s in slots)
				if (!s.isSeparator) groupsWithContent.Add(s.groupName);

			groupNames.Add(GroupViewAllKey);
			foreach (var g in seenGroups)
				if (groupsWithContent.Contains(g)) groupNames.Add(g);

			if (currentGroupIndex < 0 || currentGroupIndex >= groupNames.Count)
				currentGroupIndex = 0;
		}

		/// <summary>
		/// Pulls live weights from the renderer so undo/redo, Inspector edits and Animation / Timeline
		/// preview are always shown, and rebuilds the slots when the renderer or its mesh changed.
		/// Returns true when anything in the list changed.
		/// </summary>
		private bool RefreshFromRenderer()
		{
			Mesh mesh = targetRenderer != null ? targetRenderer.sharedMesh : null;
			if (mesh != scannedMesh)
			{
				// Preview weights were written by index into the old mesh; drop them instead of restoring.
				if (inPreview) AbandonPreview();
				RescanSlots();
				return true;
			}
			if (mesh == null || inPreview) return false;

			bool changed = false;
			int count = mesh.blendShapeCount;
			foreach (var s in slots)
			{
				if (s.index >= count) continue;
				float weight = targetRenderer.GetBlendShapeWeight(s.index);
				if (weight != s.current)
				{
					s.current = weight;
					changed = true;
				}
			}
			return changed;
		}

		/// <summary>
		/// Detects whether the blendshape name looks like a group-section header
		/// such as "---Eye---", "==Body==", "___Face", "Body===", "---體型---".
		/// Returns the extracted group name via <paramref name="groupName"/>.
		/// </summary>
		private static bool TryDetectGroupSeparator(string raw, out string groupName)
		{
			groupName = null;
			if (string.IsNullOrEmpty(raw)) return false;
			string s = raw.Trim();
			if (s.Length == 0) return false;

			int leadRun = 0;
			while (leadRun < s.Length && IsSepChar(s[leadRun])) leadRun++;

			int trailRun = 0;
			while (trailRun < s.Length - leadRun && IsSepChar(s[s.Length - 1 - trailRun])) trailRun++;

			// A "separator" must satisfy one of:
			//   - lead run >= 3 (e.g. "---Eye", "_____Face")
			//   - trail run >= 3 (e.g. "Eye---")
			//   - lead >= 2 AND trail >= 2 (e.g. "==體型==", "__Face__")
			bool qualifies =
				leadRun >= 3 ||
				trailRun >= 3 ||
				(leadRun >= 2 && trailRun >= 2);

			if (!qualifies) return false;

			int nameLen = s.Length - leadRun - trailRun;
			if (nameLen <= 0) return false;

			string inner = s.Substring(leadRun, nameLen).Trim();
			if (inner.Length == 0) return false;

			// Require at least one non-separator character in the inner name
			// so that strings like "-----" aren't mistaken for a group with empty name.
			bool hasContent = false;
			for (int i = 0; i < inner.Length; i++)
			{
				if (!IsSepChar(inner[i]) && !char.IsWhiteSpace(inner[i]))
				{
					hasContent = true;
					break;
				}
			}
			if (!hasContent) return false;

			groupName = inner;
			return true;
		}

		private static bool IsSepChar(char c)
		{
			for (int i = 0; i < SeparatorChars.Length; i++)
				if (SeparatorChars[i] == c) return true;
			return false;
		}

		private string GetGroupDisplayName(string internalKey)
		{
			if (internalKey == GroupViewAllKey) return L("bseGroupViewAll", "View All");
			if (internalKey == GroupGeneralKey) return L("bseGroupGeneral", "General");
			return internalKey;
		}
	}
}
