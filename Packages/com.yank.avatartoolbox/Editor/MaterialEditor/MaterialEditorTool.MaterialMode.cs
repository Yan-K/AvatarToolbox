using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace YanK
{
	public partial class MaterialEditorTool
	{
		private class RendererRef
		{
			public Renderer renderer;
			public int materialIndex;
		}

		private class MaterialSlot
		{
			public Material current;
			public Material original;
			// Tracks the exact (renderer, slot-index) pairs this slot was scanned from.
			// This keeps distinct slots independent even when they share the same Material asset.
			public List<RendererRef> refs = new List<RendererRef>();
			public bool foldout;
			public bool selected;

			// Convenience: unique renderer list for display.
			public IEnumerable<Renderer> renderers => refs.Select(r => r.renderer).Distinct();
			public int rendererCount => refs.Select(r => r.renderer).Distinct().Count();
		}

		private readonly List<MaterialSlot> materialSlots = new List<MaterialSlot>();
		private bool selectAll;
		private Material batchReplaceMaterial;
		[SerializeField] private List<Material> inspectedMaterials = new List<Material>();

		// --- UI ---

		private void DrawMaterialControlPanel()
		{
			GUILayout.Space(4);

			// Mode selector + Scan group
			DrawGroupBox(() =>
			{
				DrawTabBar();
				GUILayout.Space(4);
				EditorGUILayout.BeginHorizontal();
				if (GUILayout.Button(new GUIContent(L("forceScan", "Rescan Materials"), L("forceScanTooltip", "Scan the root object hierarchy for materials")), GUILayout.Height(30)))
					ScanMaterials();
				GUILayout.Space(8);
				bool newIncludeInactive = EditorGUILayout.ToggleLeft(L("includeInactive", "Include Inactive"), includeInactive, GUILayout.Width(120), GUILayout.Height(30));
				if (newIncludeInactive != includeInactive)
					SetIncludeInactive(newIncludeInactive);
				EditorGUILayout.EndHorizontal();
			});

			GUILayout.Space(4);

			// Batch operations group
			DrawGroupBox(() =>
			{
				EditorGUILayout.BeginHorizontal();
				EditorGUILayout.LabelField(L("batchActions", "Batch Actions"), GUILayout.MaxWidth(100), GUILayout.ExpandWidth(false));
				if (GUILayout.Button(new GUIContent(L("inspect", "Inspect"), L("inspectSelectedTooltip", "Switch to Texture Mode for selected materials")), GUILayout.Height(24), GUILayout.ExpandWidth(true), GUILayout.MinWidth(0)))
					InspectSelectedMaterials();
				if (GUILayout.Button(new GUIContent(L("clone", "Clone"), L("cloneSelectedTooltip", "Clone selected materials as new assets")), GUILayout.Height(24), GUILayout.ExpandWidth(true), GUILayout.MinWidth(0)))
					ConfirmBatchCloneMaterials();
				if (GUILayout.Button(new GUIContent(L("replace", "Replace"), L("replaceSelectedTooltip", "Replace selected materials with the batch material")), GUILayout.Height(24), GUILayout.ExpandWidth(true), GUILayout.MinWidth(0)))
					ConfirmBatchReplaceMaterials();
				if (GUILayout.Button(new GUIContent(L("reset", "Reset"), L("resetSelectedTooltip", "Reset selected materials to their originals")), GUILayout.Height(24), GUILayout.ExpandWidth(true), GUILayout.MinWidth(0)))
					ConfirmBatchResetMaterials();
				EditorGUILayout.EndHorizontal();

				GUILayout.Space(4);
				EditorGUILayout.BeginHorizontal();
				EditorGUILayout.LabelField(L("batchReplace", "Batch Replace"), GUILayout.MaxWidth(100), GUILayout.ExpandWidth(false));
				batchReplaceMaterial = (Material)EditorGUILayout.ObjectField(batchReplaceMaterial, typeof(Material), false);
				if (batchReplaceMaterial != null)
				{
					if (GUILayout.Button("✕", GUILayout.Width(22), GUILayout.Height(18)))
						batchReplaceMaterial = null;
				}
				EditorGUILayout.EndHorizontal();
			});

			GUILayout.Space(2);
			DrawStyledSeparator();
		}

		private void DrawMaterialList()
		{
			if (materialSlots.Count == 0)
			{
				DrawCenteredMessage(L("noMaterials", "No materials found. Drop a GameObject in the Root Object field and scan again."), "d_Material Icon");
				return;
			}

			// Search bar
			materialSearchFilter = DrawSearchField(materialSearchFilter);
			GUILayout.Space(2);

			// Select All + status
			var filtered = GetFilteredMaterialSlots();

			EditorGUILayout.BeginHorizontal();

			bool newSelectAll = EditorGUILayout.ToggleLeft(L("selectAll", "Select All"), selectAll, GUILayout.Width(100));
			if (newSelectAll != selectAll)
			{
				selectAll = newSelectAll;
				foreach (var slot in materialSlots)
					slot.selected = selectAll;
			}

			GUILayout.FlexibleSpace();

			if (!string.IsNullOrEmpty(materialSearchFilter))
				EditorGUILayout.LabelField(string.Format(L("filterStatus", "{0} of {1} shown"), filtered.Count, materialSlots.Count), statusLabelStyle);
			else
				EditorGUILayout.LabelField(string.Format(L("materialsInUse", "{0} Materials in Use"), materialSlots.Count), statusLabelStyle);

			EditorGUILayout.EndHorizontal();

			// Scrollable list
			scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));

			foreach (var slot in filtered)
			{
				GUILayout.Space(ItemSpacing);
				bool isModified = slot.current != slot.original;
				EditorGUILayout.BeginVertical(isModified ? modifiedCardStyle : cardStyle);
				DrawMaterialItem(slot, isModified);
				EditorGUILayout.EndVertical();
			}

			EditorGUILayout.EndScrollView();
		}

		private void DrawMaterialItem(MaterialSlot slot, bool isModified)
		{
			// Row 1: Checkbox + Material ObjectField + Shader label
			EditorGUILayout.BeginHorizontal();

			bool newSelected = EditorGUILayout.Toggle(slot.selected, GUILayout.Width(20));
			if (newSelected != slot.selected)
			{
				slot.selected = newSelected;
				UpdateSelectAllState();
			}

			var newMaterial = (Material)EditorGUILayout.ObjectField(slot.current, typeof(Material), false);
			if (newMaterial != null && newMaterial != slot.current)
				ReplaceMaterial(slot, newMaterial);

			// Shader name (dimmed)
			if (slot.current != null && slot.current.shader != null)
				EditorGUILayout.LabelField(slot.current.shader.name, dimLabelStyle, GUILayout.Width(140));

			if (isModified)
				GUILayout.Label(L("modified", "Modified"), EditorStyles.miniLabel, GUILayout.ExpandWidth(false));

			EditorGUILayout.EndHorizontal();

			// Row 2: Foldout + Action buttons on same line
			EditorGUILayout.BeginHorizontal();

			bool newFoldout = EditorGUILayout.Foldout(slot.foldout, string.Format(L("usedBy", "Used by {0} renderer(s)"), slot.rendererCount), true);
			if (newFoldout != slot.foldout)
				slot.foldout = newFoldout;

			GUILayout.FlexibleSpace();

			if (GUILayout.Button(L("inspect", "Inspect"), GUILayout.MinWidth(40), GUILayout.Height(20)))
				InspectMaterial(slot.current);

			if (GUILayout.Button(L("clone", "Clone"), GUILayout.MinWidth(40), GUILayout.Height(20)))
				CloneMaterial(slot);

			if (GUILayout.Button(L("reset", "Reset"), GUILayout.MinWidth(40), GUILayout.Height(20)))
				ResetMaterial(slot);

			EditorGUILayout.EndHorizontal();

			// Foldout content: renderer list
			if (newFoldout)
			{
				EditorGUI.indentLevel++;
				foreach (var renderer in slot.renderers)
					EditorGUILayout.ObjectField(renderer, typeof(Renderer), true);
				EditorGUI.indentLevel--;
			}
		}

		private List<MaterialSlot> GetFilteredMaterialSlots()
		{
			if (string.IsNullOrEmpty(materialSearchFilter))
				return materialSlots;

			string filter = materialSearchFilter.ToLowerInvariant();
			return materialSlots.Where(s =>
			{
				if (s.current == null) return false;
				if (s.current.name.ToLowerInvariant().Contains(filter)) return true;
				if (s.current.shader != null && s.current.shader.name.ToLowerInvariant().Contains(filter)) return true;
				return false;
			}).ToList();
		}

		// --- Data ---

		private void ScanMaterials()
		{
			// Keep checkbox / foldout state across rescans (undo, redo, include inactive...).
			var previous = new Dictionary<Material, MaterialSlot>();
			foreach (var s in materialSlots)
				if (s.original != null && !previous.ContainsKey(s.original)) previous[s.original] = s;

			materialSlots.Clear();
			selectAll = false;

			if (rootObject == null) return;

			if (rootObject is GameObject go)
			{
				var origins = BuildMaterialOriginLookup();
				// Grouped by (original, current) so an edited slot keeps its own original, even when its
				// current material is also used by other, unedited slots.
				var seen = new Dictionary<(Material, Material), MaterialSlot>();
				foreach (var renderer in go.GetComponentsInChildren<Renderer>(includeInactive))
				{
					var mats = renderer.sharedMaterials;
					for (int i = 0; i < mats.Length; i++)
					{
						var material = mats[i];
						if (material == null) continue;
						var original = GetOriginalMaterial(origins, renderer, i, material);
						if (!seen.TryGetValue((original, material), out var slot))
						{
							slot = new MaterialSlot { current = material, original = original };
							if (previous.TryGetValue(original, out var prev))
							{
								slot.selected = prev.selected;
								slot.foldout = prev.foldout;
							}
							seen[(original, material)] = slot;
							materialSlots.Add(slot);
						}
						slot.refs.Add(new RendererRef { renderer = renderer, materialIndex = i });
					}
				}
			}
			else if (rootObject is Material rootMat)
			{
				materialSlots.Add(new MaterialSlot { current = rootMat, original = rootMat });
			}

			UpdateSelectAllState();
		}

		// --- Operations ---

		private void ReplaceMaterial(MaterialSlot slot, Material newMaterial)
		{
			var oldMaterial = slot.current;
			// Apply by exact (renderer, index) to avoid touching other slots that happen
			// to share the same material asset on the same renderer.
			foreach (var group in slot.refs.GroupBy(r => r.renderer))
			{
				var renderer = group.Key;
				Undo.RecordObject(renderer, $"Replace Material {oldMaterial?.name} with {newMaterial?.name}");
				var mats = renderer.sharedMaterials;
				foreach (var r in group)
				{
					if (r.materialIndex < mats.Length)
						mats[r.materialIndex] = newMaterial;
				}
				renderer.sharedMaterials = mats;
				EditorUtility.SetDirty(renderer);
			}
			slot.current = newMaterial;
			RefreshUnsavedState();
			Repaint();
		}

		private void CloneMaterial(MaterialSlot slot)
		{
			string path = AssetDatabase.GetAssetPath(slot.current);
			if (string.IsNullOrEmpty(path)) return;

			string newPath = GenerateClonePath(path);
			AssetDatabase.CreateAsset(new Material(slot.current), newPath);
			AssetDatabase.SaveAssets();

			var cloned = AssetDatabase.LoadAssetAtPath<Material>(newPath);
			if (cloned != null)
				ReplaceMaterial(slot, cloned);
		}

		private void ResetMaterial(MaterialSlot slot)
		{
			if (slot.original == null || slot.original == slot.current) return;
			ReplaceMaterial(slot, slot.original);
		}

		// --- Batch Operations ---

		private void BatchReplaceSelected()
		{
			if (batchReplaceMaterial == null) return;
			foreach (var slot in materialSlots.Where(s => s.selected).ToList())
				ReplaceMaterial(slot, batchReplaceMaterial);
		}

		private void BatchCloneSelected()
		{
			foreach (var slot in materialSlots.Where(s => s.selected).ToList())
				CloneMaterial(slot);
		}

		private void BatchResetSelected()
		{
			foreach (var slot in materialSlots.Where(s => s.selected).ToList())
				ResetMaterial(slot);
		}

		private void ConfirmBatchResetMaterials()
		{
			var selected = materialSlots.Where(s => s.selected).ToList();
			if (selected.Count == 0) return;
			if (EditorUtility.DisplayDialog(
				L("confirmResetTitle", "Confirm Reset"),
				string.Format(L("confirmReset", "Reset {0} item(s) to original?"), selected.Count),
				"OK", "Cancel"))
			{
				BatchResetSelected();
			}
		}

		private void ConfirmBatchCloneMaterials()
		{
			var selected = materialSlots.Where(s => s.selected).ToList();
			if (selected.Count == 0) return;
			if (EditorUtility.DisplayDialog(
				L("confirmCloneTitle", "Confirm Clone"),
				string.Format(L("confirmClone", "Clone {0} item(s)?"), selected.Count),
				"OK", "Cancel"))
			{
				BatchCloneSelected();
			}
		}

		private void ConfirmBatchReplaceMaterials()
		{
			var selected = materialSlots.Where(s => s.selected).ToList();
			if (selected.Count == 0) return;
			if (batchReplaceMaterial == null) return;
			if (EditorUtility.DisplayDialog(
				L("confirmReplaceTitle", "Confirm Replace"),
				string.Format(L("confirmReplace", "Replace {0} item(s)?"), selected.Count),
				"OK", "Cancel"))
			{
				BatchReplaceSelected();
			}
		}

		private void InspectMaterial(Material material)
		{
			inspectedMaterials.Clear();
			inspectedMaterials.Add(material);
			currentTab = 1;
			ScanTextures();
		}

		private void InspectSelectedMaterials()
		{
			var mats = materialSlots.Where(s => s.selected).Select(s => s.current).ToList();
			if (mats.Count == 0) return;
			inspectedMaterials.Clear();
			inspectedMaterials.AddRange(mats);
			currentTab = 1;
			ScanTextures();
		}

		// --- Helpers ---

		private void UpdateSelectAllState()
		{
			selectAll = materialSlots.Count > 0 && materialSlots.All(s => s.selected);
		}
	}
}
