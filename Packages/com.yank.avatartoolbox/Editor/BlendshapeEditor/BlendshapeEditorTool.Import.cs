using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace YanK
{
	public partial class BlendshapeEditorTool
	{
		// Parsed curves from the importClip, keyed by blendshape name.
		private Dictionary<string, AnimationCurve> importCurves = new Dictionary<string, AnimationCurve>();

		// --- UI Entry ---

		// Drawn as content inside DrawAnimationTab (no foldout, no outer GroupBox).
		private void DrawImportContent()
		{
			EditorGUILayout.BeginHorizontal(GUILayout.Height(24));
			EditorGUILayout.LabelField(L("bseImportClip", "Clip"), GUILayout.Width(60), GUILayout.Height(24));
			var newClip = (AnimationClip)EditorGUILayout.ObjectField(importClip, typeof(AnimationClip), false, GUILayout.Height(24));
			EditorGUILayout.EndHorizontal();

			if (newClip != importClip)
			{
				if (inPreview) CancelPreview();
				importClip = newClip;
				if (importClip != null) EnterPreview();
			}

			GUILayout.Space(2);

			// Mode selector is always visible (even before a clip is assigned).
			string[] modeNames =
			{
				L("bseImportOverlay", "Overlay"),
				L("bseImportResetZero", "Reset Zero"),
				L("bseImportResetDefault", "Reset Default"),
				L("bseCustomMode", "Custom"),
			};
			int cur = (int)importMode;
			int newMode = GUILayout.Toolbar(cur, modeNames, GUILayout.Height(22));
			if (newMode != cur)
			{
				importMode = (ImportMode)newMode;
				if (inPreview) ApplyPreview();
			}
			EditorGUILayout.LabelField(GetImportModeDescription(importMode), dimLabelStyle);

			if (importClip == null) return;

			GUILayout.Space(4);

			// Normalized time slider for multi-keyframe clips
			if (importClipHasMultipleKeyframes)
			{
				EditorGUILayout.BeginHorizontal();
				EditorGUILayout.LabelField(L("bseNormalizedTime", "Time"), GUILayout.Width(60));
				EditorGUI.BeginChangeCheck();
				float newT = EditorGUILayout.Slider(importNormalizedTime, 0f, 1f);
				if (EditorGUI.EndChangeCheck())
				{
					importNormalizedTime = newT;
					if (inPreview) ApplyPreview();
				}
				EditorGUILayout.LabelField(string.Format("{0:0.00}s / {1:0.00}s", importClip.length * importNormalizedTime, importClip.length), dimLabelStyle, GUILayout.Width(90));
				EditorGUILayout.EndHorizontal();
			}

			GUILayout.Space(2);

			if (importMode == ImportMode.Custom)
				DrawCustomImportPicker();

			if (CountMissingRemap() > 0)
				DrawRemapPanel();

			GUILayout.Space(4);

			// Apply / Cancel
			EditorGUILayout.BeginHorizontal();
			if (GUILayout.Button(L("bseApply", "Apply"), GUILayout.Height(26)))
				CommitPreview();
			if (GUILayout.Button(L("bseCancelPreview", "Cancel"), GUILayout.Height(26)))
			{
				CancelPreview();
				importClip = null;
			}
			EditorGUILayout.EndHorizontal();
		}

		private string GetImportModeDescription(ImportMode m)
		{
			switch (m)
			{
				case ImportMode.Overlay: return L("bseImportOverlayDesc", "Keep existing values; only overwrite blendshapes present in the clip.");
				case ImportMode.ResetZero: return L("bseImportResetZeroDesc", "Set all blendshapes to 0, then apply the clip.");
				case ImportMode.ResetDefault: return L("bseImportResetDefaultDesc", "Reset to default (scan-time) values, then apply the clip.");
				case ImportMode.Custom: return L("bseImportCustomDesc", "Choose which source blendshapes to import.");
			}
			return "";
		}

		// --- Preview Lifecycle ---

		private void EnterPreview()
		{
			if (targetRenderer == null || importClip == null) return;

			// Snapshot existing values so we can cancel cleanly.
			previewSnapshot = new Dictionary<int, float>();
			foreach (var s in slots)
			{
				if (s.isSeparator) continue;
				previewSnapshot[s.index] = s.current;
			}

			ParseImportClip();
			BuildRemapFromClip();
			importNormalizedTime = 0f;

			// Default: all source blendshapes checked for Custom import
			customImportNames.Clear();
			foreach (var kv in importCurves) customImportNames.Add(kv.Key);

			inPreview = true;
			ApplyPreview();
		}

		private void CancelPreview()
		{
			if (!inPreview) return;
			RestoreSnapshot();
			inPreview = false;
			importCurves.Clear();
			remapEntries.Clear();
			previewSnapshot = null;
		}

		private void CommitPreview()
		{
			if (!inPreview || targetRenderer == null) return;

			// Capture current preview values and write them through with proper Undo.
			var committed = new Dictionary<int, float>();
			foreach (var s in slots)
			{
				if (s.isSeparator) continue;
				committed[s.index] = s.current;
			}

			// Restore to snapshot first so RegisterCompleteObjectUndo captures original state for proper Undo.
			RestoreSnapshot();

			Undo.RegisterCompleteObjectUndo(targetRenderer, "YBE: Import Blendshape Animation");
			foreach (var kv in committed)
			{
				// Update both data model and the renderer.
				foreach (var s in slots)
					if (!s.isSeparator && s.index == kv.Key) s.current = kv.Value;
				targetRenderer.SetBlendShapeWeight(kv.Key, kv.Value);
			}
			EditorUtility.SetDirty(targetRenderer);

			inPreview = false;
			importCurves.Clear();
			remapEntries.Clear();
			previewSnapshot = null;
			importClip = null;
		}

		private void RestoreSnapshot()
		{
			if (previewSnapshot == null || targetRenderer == null) return;
			foreach (var kv in previewSnapshot)
			{
				targetRenderer.SetBlendShapeWeight(kv.Key, kv.Value);
				foreach (var s in slots)
					if (!s.isSeparator && s.index == kv.Key) s.current = kv.Value;
			}
		}

		// --- Clip Parsing ---

		private void ParseImportClip()
		{
			importCurves.Clear();
			importClipHasMultipleKeyframes = false;
			if (importClip == null) return;

			foreach (var binding in AnimationUtility.GetCurveBindings(importClip))
			{
				if (binding.type != typeof(SkinnedMeshRenderer)) continue;
				if (!binding.propertyName.StartsWith("blendShape.")) continue;

				string bsName = binding.propertyName.Substring("blendShape.".Length);
				var curve = AnimationUtility.GetEditorCurve(importClip, binding);
				if (curve == null) continue;

				// Later bindings with the same name overwrite earlier ones.
				importCurves[bsName] = curve;
				if (curve.keys != null && curve.keys.Length > 1) importClipHasMultipleKeyframes = true;
			}

			if (importClip.length > 0f && importCurves.Count > 0)
			{
				// Even a clip with single key per curve but non-zero length may appear static; show slider only if multiple keys exist.
			}
		}

		// --- Remap ---

		private void BuildRemapFromClip()
		{
			remapEntries.Clear();
			if (importCurves == null) return;

			foreach (var kv in importCurves)
			{
				string srcName = kv.Key;
				int match = FindSlotIndexByName(srcName);
				if (match < 0)
				{
					remapEntries.Add(new RemapEntry
					{
						sourceName = srcName,
						targetSlotIndex = -1,
						sourceValue = 0f,
					});
				}
			}
		}

		private int FindSlotIndexByName(string name)
		{
			for (int i = 0; i < slots.Count; i++)
				if (!slots[i].isSeparator && slots[i].name == name) return i;
			return -1;
		}

		private int CountMissingRemap() => remapEntries.Count;

		private void DrawRemapPanel()
		{
			// In Custom mode only show remap for entries whose source name is checked above.
			List<RemapEntry> visible;
			if (importMode == ImportMode.Custom)
			{
				visible = new List<RemapEntry>(remapEntries.Count);
				foreach (var e in remapEntries)
					if (customImportNames.Contains(e.sourceName)) visible.Add(e);
			}
			else
			{
				visible = remapEntries;
			}
			if (visible.Count == 0) return;

			EditorGUILayout.LabelField(
				string.Format(L("bseRemapTitle", "Remap Missing Blendshapes ({0})"), visible.Count),
				EditorStyles.boldLabel);

			if (GUILayout.Button(L("bseRemapAutoMatch", "Auto-match Similar Names"), GUILayout.Height(20)))
			{
				AutoMatchRemap();
				if (inPreview) ApplyPreview();
			}

			// Decide whether to use the searchable AdvancedDropdown or a flat Popup.
			// A flat popup is fine when the slot count is tiny (avoids extra window flash).
			int totalSlots = 0;
			foreach (var s in slots) if (!s.isSeparator) totalSlots++;
			bool useAdvancedDropdown = totalSlots > 20;

			// Build a shared "non-separator slot list" for both paths.
			var nonSepSlots = new List<BlendshapeSlot>(totalSlots);
			foreach (var s in slots) if (!s.isSeparator) nonSepSlots.Add(s);

			// Fallback flat-popup labels (only built when needed).
			string[] popupLabels = null;
			if (!useAdvancedDropdown)
			{
				popupLabels = new string[nonSepSlots.Count + 1];
				popupLabels[0] = L("bseRemapIgnore", "(Ignore)");
				for (int i = 0; i < nonSepSlots.Count; i++) popupLabels[i + 1] = nonSepSlots[i].name;
			}

			float remapH = Mathf.Clamp(visible.Count * 22f + 8f, 60f, 280f);
			remapScroll = EditorGUILayout.BeginScrollView(remapScroll, GUILayout.Height(remapH));
			foreach (var entry in visible)
			{
				EditorGUILayout.BeginHorizontal();
				EditorGUILayout.LabelField(entry.sourceName, GUILayout.MinWidth(120));
				EditorGUILayout.LabelField("→", GUILayout.Width(16));

				string currentLabel = entry.targetSlotIndex < 0
					? L("bseRemapIgnore", "(Ignore)")
					: slots[entry.targetSlotIndex].name;

				if (useAdvancedDropdown)
				{
					if (EditorGUILayout.DropdownButton(new GUIContent(currentLabel), FocusType.Passive))
					{
						var rect = GUILayoutUtility.GetLastRect();
						var captured = entry; // closure capture
						ShowRemapDropdown(rect, newTarget =>
						{
							captured.targetSlotIndex = newTarget;
							if (inPreview) ApplyPreview();
							Repaint();
						});
					}
				}
				else
				{
					int popupIndex = 0;
					if (entry.targetSlotIndex >= 0)
					{
						int sIdx = nonSepSlots.FindIndex(s => slots.IndexOf(s) == entry.targetSlotIndex);
						if (sIdx >= 0) popupIndex = sIdx + 1;
					}
					int newPopup = EditorGUILayout.Popup(popupIndex, popupLabels);
					if (newPopup != popupIndex)
					{
						entry.targetSlotIndex = (newPopup == 0) ? -1 : slots.IndexOf(nonSepSlots[newPopup - 1]);
						if (inPreview) ApplyPreview();
					}
				}
				EditorGUILayout.EndHorizontal();
			}
			EditorGUILayout.EndScrollView();
		}

		private void AutoMatchRemap()
		{
			foreach (var entry in remapEntries)
			{
				if (entry.targetSlotIndex >= 0) continue;
				int best = -1;
				int bestDist = int.MaxValue;
				string srcLower = entry.sourceName.ToLowerInvariant();

				for (int i = 0; i < slots.Count; i++)
				{
					var s = slots[i];
					if (s.isSeparator) continue;
					string candLower = s.name.ToLowerInvariant();

					// Priority 1: Contains (either direction) on lowered names
					if (candLower.Contains(srcLower) || srcLower.Contains(candLower))
					{
						int d = Mathf.Abs(candLower.Length - srcLower.Length);
						if (d < bestDist) { bestDist = d; best = i; }
						continue;
					}

					// Priority 2: Levenshtein distance with threshold
					int lev = LevenshteinDistance(srcLower, candLower);
					int threshold = Mathf.Max(3, srcLower.Length / 3);
					if (lev <= threshold && lev + 10 < bestDist)
					{
						bestDist = lev + 10; // rank below contains matches
						best = i;
					}
				}

				if (best >= 0) entry.targetSlotIndex = best;
			}
		}

		private static int LevenshteinDistance(string a, string b)
		{
			if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
			if (string.IsNullOrEmpty(b)) return a.Length;
			var d = new int[a.Length + 1, b.Length + 1];
			for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
			for (int j = 0; j <= b.Length; j++) d[0, j] = j;
			for (int i = 1; i <= a.Length; i++)
			{
				for (int j = 1; j <= b.Length; j++)
				{
					int cost = a[i - 1] == b[j - 1] ? 0 : 1;
					d[i, j] = Mathf.Min(
						Mathf.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
						d[i - 1, j - 1] + cost);
				}
			}
			return d[a.Length, b.Length];
		}

		// --- Custom picker ---

		private void DrawCustomImportPicker()
		{
			EditorGUILayout.LabelField(
				string.Format(L("bseCustomPick", "Pick source blendshapes ({0} total)"), importCurves.Count),
				EditorStyles.boldLabel);

			// Search field
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField(L("bseCustomSearch", "Search"), GUILayout.Width(60));
			customImportFilter = EditorGUILayout.TextField(customImportFilter ?? "");
			if (GUILayout.Button("✕", GUILayout.Width(22)))
				customImportFilter = "";
			EditorGUILayout.EndHorizontal();

			// Build filtered source name list
			string filter = string.IsNullOrEmpty(customImportFilter) ? null : customImportFilter.ToLowerInvariant();
			var filteredNames = new List<string>(importCurves.Count);
			foreach (var kv in importCurves)
			{
				if (filter != null && kv.Key.ToLowerInvariant().IndexOf(filter, System.StringComparison.Ordinal) < 0) continue;
				filteredNames.Add(kv.Key);
			}

			// All / None / Invert / Zero / NonZero — scoped to filtered set only.
			float sampleTimeForOps = importClip != null ? importClip.length * Mathf.Clamp01(importNormalizedTime) : 0f;
			EditorGUILayout.BeginHorizontal();
			if (GUILayout.Button(L("bseCustomAll", "All"), GUILayout.Width(54)))
			{
				foreach (var n in filteredNames) customImportNames.Add(n);
				if (inPreview) ApplyPreview();
			}
			if (GUILayout.Button(L("bseCustomNone", "None"), GUILayout.Width(54)))
			{
				foreach (var n in filteredNames) customImportNames.Remove(n);
				if (inPreview) ApplyPreview();
			}
			if (GUILayout.Button(L("bseCustomInvert", "Invert"), GUILayout.Width(54)))
			{
				foreach (var n in filteredNames)
				{
					if (customImportNames.Contains(n)) customImportNames.Remove(n);
					else customImportNames.Add(n);
				}
				if (inPreview) ApplyPreview();
			}
			// Scope picks to rows whose sampled value is (approximately) zero / non-zero in the clip.
			if (GUILayout.Button(L("bseCustomZero", "Zero"), GUILayout.Width(54)))
			{
				foreach (var n in filteredNames)
				{
					if (!importCurves.TryGetValue(n, out var c) || c == null) continue;
					if (Mathf.Abs(c.Evaluate(sampleTimeForOps)) <= 0.001f) customImportNames.Add(n);
					else customImportNames.Remove(n);
				}
				if (inPreview) ApplyPreview();
			}
			if (GUILayout.Button(L("bseCustomNonZero", "Non-Zero"), GUILayout.Width(70)))
			{
				foreach (var n in filteredNames)
				{
					if (!importCurves.TryGetValue(n, out var c) || c == null) continue;
					if (Mathf.Abs(c.Evaluate(sampleTimeForOps)) > 0.001f) customImportNames.Add(n);
					else customImportNames.Remove(n);
				}
				if (inPreview) ApplyPreview();
			}
			GUILayout.FlexibleSpace();

			// Status line: checked / total (filtered)
			int checkedInFiltered = 0;
			foreach (var n in filteredNames) if (customImportNames.Contains(n)) checkedInFiltered++;
			EditorGUILayout.LabelField(
				string.Format(L("bseCustomSelected", "{0} / {1} selected"), checkedInFiltered, filteredNames.Count),
				dimLabelStyle);
			EditorGUILayout.EndHorizontal();

			// Sample time for showing per-row values.
			float sampleTime = importClip != null ? importClip.length * Mathf.Clamp01(importNormalizedTime) : 0f;

			float pickerH = Mathf.Clamp(filteredNames.Count * 20f + 8f, 80f, 400f);
			customImportScroll = EditorGUILayout.BeginScrollView(customImportScroll, GUILayout.Height(pickerH));
			foreach (var name in filteredNames)
			{
				EditorGUILayout.BeginHorizontal();
				bool was = customImportNames.Contains(name);
				bool now = EditorGUILayout.Toggle(was, GUILayout.Width(18));
				if (now != was)
				{
					if (now) customImportNames.Add(name);
					else customImportNames.Remove(name);
					if (inPreview) ApplyPreview();
				}
				EditorGUILayout.LabelField(name, GUILayout.MinWidth(80));

				// Sample value from curve for context.
				if (importCurves.TryGetValue(name, out var curve) && curve != null)
				{
					float sampled = curve.Evaluate(sampleTime);
					EditorGUILayout.LabelField(string.Format("{0:0.##}", sampled), dimLabelStyle, GUILayout.Width(60));
				}
				EditorGUILayout.EndHorizontal();
			}
			EditorGUILayout.EndScrollView();
		}

		// --- Preview application (recomputed on any change) ---

		private void ApplyPreview()
		{
			if (!inPreview || targetRenderer == null || importClip == null || previewSnapshot == null) return;

			float sampleTime = importClip.length * Mathf.Clamp01(importNormalizedTime);

			// Compute resolved: slotIndex -> value, respecting remap + custom mode filter.
			var resolved = new Dictionary<int, float>();
			foreach (var kv in importCurves)
			{
				string srcName = kv.Key;

				if (importMode == ImportMode.Custom && !customImportNames.Contains(srcName)) continue;

				int slotIdx = FindSlotIndexByName(srcName);
				if (slotIdx < 0)
				{
					// Look up remap
					foreach (var entry in remapEntries)
					{
						if (entry.sourceName == srcName)
						{
							slotIdx = entry.targetSlotIndex;
							break;
						}
					}
				}
				if (slotIdx < 0) continue;

				float val = kv.Value.Evaluate(sampleTime);
				resolved[slots[slotIdx].index] = val;
			}

			// Compute base + apply to every slot
			foreach (var s in slots)
			{
				if (s.isSeparator) continue;

				float baseVal;
				switch (importMode)
				{
					case ImportMode.ResetZero: baseVal = 0f; break;
					case ImportMode.ResetDefault: baseVal = s.defaultValue; break;
					default:
						baseVal = previewSnapshot.TryGetValue(s.index, out var v) ? v : s.current;
						break;
				}

				float finalVal = resolved.TryGetValue(s.index, out var rv) ? rv : baseVal;
				s.current = finalVal;
				targetRenderer.SetBlendShapeWeight(s.index, finalVal);
			}
			// Intentionally no SetDirty / no Undo during preview.
			Repaint();
		}
	}
}
