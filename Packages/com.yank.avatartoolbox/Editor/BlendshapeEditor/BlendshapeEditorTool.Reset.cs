using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace YanK
{
	public partial class BlendshapeEditorTool
	{
		// --- Writing weights ---

		/// <summary>
		/// Writes weights through SerializedObject, the same path the SkinnedMeshRenderer Inspector uses,
		/// so every edit is undoable, saved as a prefab override, and keyed by Animation / Timeline recording.
		/// </summary>
		private void WriteWeights(List<KeyValuePair<BlendshapeSlot, float>> edits, string undoName)
		{
			if (targetRenderer == null || edits.Count == 0) return;
			var mesh = targetRenderer.sharedMesh;
			if (mesh == null) return;
			int count = mesh.blendShapeCount;

			var so = new SerializedObject(targetRenderer);
			var weights = so.FindProperty("m_BlendShapeWeights");
			if (weights != null && weights.isArray)
			{
				// The serialized array can be shorter than the mesh's blendshape list. Grow it first without
				// Undo so recording doesn't key every blendshape; new entries keep their current weights.
				int size = weights.arraySize;
				if (size < count)
				{
					weights.arraySize = count;
					for (int i = size; i < count; i++)
						weights.GetArrayElementAtIndex(i).floatValue = targetRenderer.GetBlendShapeWeight(i);
					so.ApplyModifiedPropertiesWithoutUndo();
					so.Update();
					weights = so.FindProperty("m_BlendShapeWeights");
				}

				foreach (var edit in edits)
				{
					if (edit.Key.index < count)
						weights.GetArrayElementAtIndex(edit.Key.index).floatValue = edit.Value;
				}
				if (so.ApplyModifiedProperties())
					Undo.SetCurrentGroupName(undoName);
			}
			else
			{
				Undo.RecordObject(targetRenderer, undoName);
				foreach (var edit in edits)
				{
					if (edit.Key.index < count)
						targetRenderer.SetBlendShapeWeight(edit.Key.index, edit.Value);
				}
			}

			foreach (var edit in edits)
			{
				if (edit.Key.index < count)
					edit.Key.current = targetRenderer.GetBlendShapeWeight(edit.Key.index);
			}
			RefreshUnsavedState();
		}

		// Writes value(s) to every non-separator slot matching filter whose weight actually changes.
		private void WriteWeights(Func<BlendshapeSlot, bool> filter, Func<BlendshapeSlot, float> value, string undoName)
		{
			var edits = new List<KeyValuePair<BlendshapeSlot, float>>();
			foreach (var s in slots)
			{
				if (s.isSeparator || !filter(s)) continue;
				float v = value(s);
				if (v != s.current) edits.Add(new KeyValuePair<BlendshapeSlot, float>(s, v));
			}
			WriteWeights(edits, undoName);
		}

		// --- Session edits (yellow rows) ---

		private static bool IsModified(BlendshapeSlot s, float weight) => Mathf.Abs(weight - s.defaultValue) > ModifiedThreshold;

		// The weight the scene keeps: while previewing an import, that's the pre-preview snapshot.
		private float CommittedWeight(BlendshapeSlot s)
		{
			if (inPreview && previewSnapshot != null && previewSnapshot.TryGetValue(s.index, out float v)) return v;
			return s.current;
		}

		protected override bool HasUnsavedEdits()
		{
			// Animation / Timeline preview drives the weights temporarily; those aren't scene edits.
			if (targetRenderer == null || AnimationMode.InAnimationMode()) return false;
			foreach (var s in slots)
			{
				if (!s.isSeparator && IsModified(s, CommittedWeight(s))) return true;
			}
			return false;
		}

		protected override void SaveEdits()
		{
			if (inPreview) CancelPreview();
			ApplyCurrentAsDefault();
		}

		protected override void RevertEdits()
		{
			if (inPreview) CancelPreview();
			WriteWeights(s => IsModified(s, s.current), s => s.defaultValue, "YBE: Revert to Original");
		}

		// --- Reset / Apply / Cancel ---

		private void ConfirmResetAllZero()
		{
			if (targetRenderer == null) return;
			if (!EditorUtility.DisplayDialog(
				L("bseResetConfirmTitle", "Confirm Reset"),
				L("bseResetZeroConfirm", "Set all blendshape weights to 0?"),
				"OK", "Cancel")) return;

			if (inPreview) CancelPreview();
			WriteWeights(s => true, s => 0f, "YBE: Reset All to Zero");
		}

		private void ConfirmResetDefault()
		{
			if (targetRenderer == null) return;
			if (!EditorUtility.DisplayDialog(
				L("bseResetConfirmTitle", "Confirm Reset"),
				L("bseResetDefaultConfirm", "Reset all blendshapes to the values they had when the renderer was first assigned?"),
				"OK", "Cancel")) return;

			if (inPreview) CancelPreview();
			WriteWeights(s => true, s => s.defaultValue, "YBE: Reset to Default");
		}

		// Sets the current values as the new originals, clearing the modified (yellow) state.
		// Metadata only — the renderer weights are already correct.
		private void ApplyCurrentAsDefault()
		{
			originalsRenderer = targetRenderer;
			originalNames.Clear();
			originalWeights.Clear();
			foreach (var s in slots)
			{
				s.defaultValue = s.current;
				originalNames.Add(s.name);
				originalWeights.Add(s.current);
			}
			RefreshUnsavedState();
		}

		// Reverts all modified blendshapes back to their originals.
		private void CancelAllToDefault()
		{
			if (targetRenderer == null) return;
			if (inPreview) CancelPreview();
			WriteWeights(s => IsModified(s, s.current), s => s.defaultValue, "YBE: Cancel Changes");
		}

		// Resets currently selected blendshapes to 0.
		private void ResetSelectedToZero()
		{
			if (targetRenderer == null) return;
			WriteWeights(s => s.selected, s => 0f, "YBE: Reset Selected to Zero");
		}

		// Resets currently selected blendshapes to their originals.
		private void ResetSelectedToDefault()
		{
			if (targetRenderer == null) return;
			WriteWeights(s => s.selected, s => s.defaultValue, "YBE: Reset Selected to Default");
		}
	}
}
