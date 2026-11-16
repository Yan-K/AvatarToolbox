using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace YanK
{
	public partial class MaterialEditorTool
	{
		private struct TextureUsageInfo
		{
			public Material material;
			public string propertyName;
		}

		private class TextureSlot
		{
			public Texture current;
			public Texture original;
			public List<TextureUsageInfo> usages = new List<TextureUsageInfo>();
			public bool foldout;
			public bool selected;
		}

		private readonly List<TextureSlot> textureSlots = new List<TextureSlot>();
		private bool textureSelectAll;
		private Texture batchReplaceTexture;
		private Vector2 textureScrollPosition;

		// --- UI ---

		private void DrawTextureControlPanel()
		{
			GUILayout.Space(4);

			// Mode selector + Scan group
			DrawGroupBox(() =>
			{
				DrawTabBar();
				GUILayout.Space(4);
				EditorGUILayout.BeginHorizontal();
				if (GUILayout.Button(new GUIContent(L("rescanTextures", "Rescan Textures"), L("rescanTexturesTooltip", "Scan the root object hierarchy for textures")), GUILayout.Height(30)))
				{
					inspectedMaterials.Clear();
					ScanTextures();
				}
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
				if (GUILayout.Button(new GUIContent(L("clone", "Clone"), L("cloneSelectedTooltip", "Clone selected textures as new assets")), GUILayout.Height(24), GUILayout.ExpandWidth(true), GUILayout.MinWidth(0)))
					ConfirmBatchCloneTextures();
				if (GUILayout.Button(new GUIContent(L("replace", "Replace"), L("replaceSelectedTooltip", "Replace selected textures with the batch texture")), GUILayout.Height(24), GUILayout.ExpandWidth(true), GUILayout.MinWidth(0)))
					ConfirmBatchReplaceTextures();
				if (GUILayout.Button(new GUIContent(L("reset", "Reset"), L("resetSelectedTooltip", "Reset selected textures to their originals")), GUILayout.Height(24), GUILayout.ExpandWidth(true), GUILayout.MinWidth(0)))
					ConfirmBatchResetTextures();
				EditorGUILayout.EndHorizontal();

				GUILayout.Space(4);
				EditorGUILayout.BeginHorizontal();
				EditorGUILayout.LabelField(L("batchReplace", "Batch Replace"), GUILayout.MaxWidth(100), GUILayout.ExpandWidth(false));
				batchReplaceTexture = (Texture)EditorGUILayout.ObjectField(batchReplaceTexture, typeof(Texture), false);
				if (batchReplaceTexture != null)
				{
					if (GUILayout.Button("✕", GUILayout.Width(22), GUILayout.Height(18)))
						batchReplaceTexture = null;
				}
				EditorGUILayout.EndHorizontal();
			});

			GUILayout.Space(2);
			DrawStyledSeparator();
		}

		private void DrawTextureList()
		{
			if (textureSlots.Count == 0)
			{
				DrawCenteredMessage(L("noTextures", "No textures found. Drop a GameObject or Material in the Root Object field and scan again."), "d_Texture Icon");
				return;
			}

			// Search bar
			textureSearchFilter = DrawSearchField(textureSearchFilter);
			GUILayout.Space(2);

			// Select All + status
			var filtered = GetFilteredTextureSlots();

			EditorGUILayout.BeginHorizontal();

			bool newSelectAll = EditorGUILayout.ToggleLeft(L("selectAll", "Select All"), textureSelectAll, GUILayout.Width(100));
			if (newSelectAll != textureSelectAll)
			{
				textureSelectAll = newSelectAll;
				foreach (var slot in textureSlots)
					slot.selected = textureSelectAll;
			}

			GUILayout.FlexibleSpace();

			if (!string.IsNullOrEmpty(textureSearchFilter))
				EditorGUILayout.LabelField(string.Format(L("filterStatus", "{0} of {1} shown"), filtered.Count, textureSlots.Count), statusLabelStyle);
			else if (inspectedMaterials.Count > 0)
			{
				EditorGUILayout.LabelField(
					string.Format(L("inspecting", "Inspecting {0} material(s)"), inspectedMaterials.Count),
					statusLabelStyle);
				if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(20)))
				{
					inspectedMaterials.Clear();
					ScanTextures();
				}
			}
			else
				EditorGUILayout.LabelField(string.Format(L("texturesInUse", "{0} Textures in Use"), textureSlots.Count), statusLabelStyle);

			EditorGUILayout.EndHorizontal();

			// Scrollable list
			textureScrollPosition = EditorGUILayout.BeginScrollView(textureScrollPosition, GUILayout.ExpandHeight(true));

			foreach (var slot in filtered)
			{
				GUILayout.Space(ItemSpacing);
				bool isModified = slot.current != slot.original;
				EditorGUILayout.BeginVertical(isModified ? modifiedCardStyle : cardStyle);
				DrawTextureItem(slot, isModified);
				EditorGUILayout.EndVertical();
			}

			EditorGUILayout.EndScrollView();
		}

		private void DrawTextureItem(TextureSlot slot, bool isModified)
		{
			// Row 1: Checkbox + Texture ObjectField + dimensions
			EditorGUILayout.BeginHorizontal();

			bool newSelected = EditorGUILayout.Toggle(slot.selected, GUILayout.Width(20));
			if (newSelected != slot.selected)
			{
				slot.selected = newSelected;
				UpdateTextureSelectAllState();
			}

			var newTexture = (Texture)EditorGUILayout.ObjectField(slot.current, typeof(Texture), false);
			if (newTexture != null && newTexture != slot.current)
				ReplaceTexture(slot, newTexture);

			// Texture dimensions (dimmed)
			if (slot.current != null)
				EditorGUILayout.LabelField($"{slot.current.width}x{slot.current.height}", dimLabelStyle, GUILayout.Width(80));

			if (isModified)
				GUILayout.Label(L("modified", "Modified"), EditorStyles.miniLabel, GUILayout.ExpandWidth(false));

			EditorGUILayout.EndHorizontal();

			// Row 2: Foldout + Action buttons on same line
			EditorGUILayout.BeginHorizontal();

			string foldoutLabel = string.Format(L("usedByMaterials", "Used by {0} material(s)"), slot.usages.Select(u => u.material).Distinct().Count());
			bool newFoldout = EditorGUILayout.Foldout(slot.foldout, foldoutLabel, true);
			if (newFoldout != slot.foldout)
				slot.foldout = newFoldout;

			GUILayout.FlexibleSpace();

			if (GUILayout.Button(L("clone", "Clone"), GUILayout.MinWidth(40), GUILayout.Height(20)))
				CloneTexture(slot);

			if (GUILayout.Button(L("reset", "Reset"), GUILayout.MinWidth(40), GUILayout.Height(20)))
				ResetTexture(slot);

			EditorGUILayout.EndHorizontal();

			// Foldout content: usage list
			if (newFoldout)
			{
				EditorGUI.indentLevel++;
				foreach (var usage in slot.usages)
				{
					EditorGUILayout.BeginHorizontal();
					EditorGUILayout.ObjectField(usage.material, typeof(Material), false);
					EditorGUILayout.LabelField(usage.propertyName, dimLabelStyle, GUILayout.Width(150));
					EditorGUILayout.EndHorizontal();
				}
				EditorGUI.indentLevel--;
			}
		}

		private List<TextureSlot> GetFilteredTextureSlots()
		{
			if (string.IsNullOrEmpty(textureSearchFilter))
				return textureSlots;

			string filter = textureSearchFilter.ToLowerInvariant();
			return textureSlots.Where(s =>
			{
				if (s.current == null) return false;
				if (s.current.name.ToLowerInvariant().Contains(filter)) return true;
				if (s.usages.Any(u => u.propertyName.ToLowerInvariant().Contains(filter))) return true;
				return false;
			}).ToList();
		}

		// --- Data ---

		private void ScanTextures()
		{
			// Keep checkbox / foldout state across rescans (undo, redo, include inactive...).
			var previous = new Dictionary<Texture, TextureSlot>();
			foreach (var s in textureSlots)
				if (s.original != null && !previous.ContainsKey(s.original)) previous[s.original] = s;

			textureSlots.Clear();
			textureSelectAll = false;

			var materials = new List<Material>();

			if (inspectedMaterials.Count > 0)
			{
				materials.AddRange(inspectedMaterials);
			}
			else if (rootObject is GameObject go)
			{
				var seen = new HashSet<Material>();
				foreach (var renderer in go.GetComponentsInChildren<Renderer>(includeInactive))
				{
					foreach (var mat in renderer.sharedMaterials)
					{
						if (mat != null && seen.Add(mat))
							materials.Add(mat);
					}
				}
			}
			else if (rootObject is Material rootMat)
			{
				materials.Add(rootMat);
			}

			var origins = BuildTextureOriginLookup();
			// Grouped by (original, current), like material slots.
			var seenTextures = new Dictionary<(Texture, Texture), TextureSlot>();
			foreach (var material in materials)
			{
				if (material == null || material.shader == null) continue;
				var shader = material.shader;
				int propCount = ShaderUtil.GetPropertyCount(shader);
				for (int i = 0; i < propCount; i++)
				{
					if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv)
						continue;

					string propName = ShaderUtil.GetPropertyName(shader, i);
					var texture = material.GetTexture(propName);
					if (texture == null) continue;

					var original = GetOriginalTexture(origins, material, propName, texture);
					if (!seenTextures.TryGetValue((original, texture), out var slot))
					{
						slot = new TextureSlot { current = texture, original = original };
						if (previous.TryGetValue(original, out var prev))
						{
							slot.selected = prev.selected;
							slot.foldout = prev.foldout;
						}
						seenTextures[(original, texture)] = slot;
						textureSlots.Add(slot);
					}
					slot.usages.Add(new TextureUsageInfo { material = material, propertyName = propName });
				}
			}

			UpdateTextureSelectAllState();
		}

		// --- Operations ---

		private void ReplaceTexture(TextureSlot slot, Texture newTexture)
		{
			var oldTexture = slot.current;
			foreach (var usage in slot.usages)
			{
				Undo.RecordObject(usage.material, $"Replace Texture {oldTexture.name} with {newTexture.name}");
				usage.material.SetTexture(usage.propertyName, newTexture);
				EditorUtility.SetDirty(usage.material);
			}
			slot.current = newTexture;
			RefreshUnsavedState();
			Repaint();
		}

		private void CloneTexture(TextureSlot slot)
		{
			string path = AssetDatabase.GetAssetPath(slot.current);
			if (string.IsNullOrEmpty(path)) return;

			string newPath = GenerateClonePath(path);
			AssetDatabase.CopyAsset(path, newPath);
			AssetDatabase.SaveAssets();

			var newTexture = AssetDatabase.LoadAssetAtPath<Texture>(newPath);
			if (newTexture != null)
				ReplaceTexture(slot, newTexture);
		}

		private void ResetTexture(TextureSlot slot)
		{
			if (slot.original == null || slot.original == slot.current) return;
			ReplaceTexture(slot, slot.original);
		}

		// --- Batch Operations ---

		private void BatchReplaceSelectedTextures()
		{
			if (batchReplaceTexture == null) return;
			foreach (var slot in textureSlots.Where(s => s.selected).ToList())
				ReplaceTexture(slot, batchReplaceTexture);
		}

		private void BatchCloneSelectedTextures()
		{
			foreach (var slot in textureSlots.Where(s => s.selected).ToList())
				CloneTexture(slot);
		}

		private void BatchResetSelectedTextures()
		{
			foreach (var slot in textureSlots.Where(s => s.selected).ToList())
				ResetTexture(slot);
		}

		private void ConfirmBatchResetTextures()
		{
			var selected = textureSlots.Where(s => s.selected).ToList();
			if (selected.Count == 0) return;
			if (EditorUtility.DisplayDialog(
				L("confirmResetTitle", "Confirm Reset"),
				string.Format(L("confirmReset", "Reset {0} item(s) to original?"), selected.Count),
				"OK", "Cancel"))
			{
				BatchResetSelectedTextures();
			}
		}

		private void ConfirmBatchCloneTextures()
		{
			var selected = textureSlots.Where(s => s.selected).ToList();
			if (selected.Count == 0) return;
			if (EditorUtility.DisplayDialog(
				L("confirmCloneTitle", "Confirm Clone"),
				string.Format(L("confirmClone", "Clone {0} item(s)?"), selected.Count),
				"OK", "Cancel"))
			{
				BatchCloneSelectedTextures();
			}
		}

		private void ConfirmBatchReplaceTextures()
		{
			var selected = textureSlots.Where(s => s.selected).ToList();
			if (selected.Count == 0) return;
			if (batchReplaceTexture == null) return;
			if (EditorUtility.DisplayDialog(
				L("confirmReplaceTitle", "Confirm Replace"),
				string.Format(L("confirmReplace", "Replace {0} item(s)?"), selected.Count),
				"OK", "Cancel"))
			{
				BatchReplaceSelectedTextures();
			}
		}

		// --- Helpers ---

		private void UpdateTextureSelectAllState()
		{
			textureSelectAll = textureSlots.Count > 0 && textureSlots.All(s => s.selected);
		}
	}
}
