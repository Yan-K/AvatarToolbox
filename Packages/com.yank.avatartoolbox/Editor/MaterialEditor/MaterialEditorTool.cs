using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace YanK
{
	public partial class MaterialEditorTool : YanKEditorWindow
	{
		protected override string ToolTitleKey => "ymeTitle";
		protected override string ToolTitleDefault => "Yan-K Material Editor";

		// Single source of truth for the YME include-inactive EditorPref key.
		private const string IncludeInactivePrefKey = "YME_IncludeInactive";

		// Serialized so the target survives script reloads.
		[SerializeField] private UnityEngine.Object rootObject;
		[SerializeField] private int currentTab;
		private Vector2 scrollPosition;
		private bool includeInactive;
		private bool needsRescan; // Slots aren't serialized; rebuild them after a reload.

		private void SetIncludeInactive(bool value)
		{
			if (includeInactive == value) return;
			includeInactive = value;
			EditorPrefs.SetBool(IncludeInactivePrefKey, value);
		}

		private string materialSearchFilter = "";
		private string textureSearchFilter = "";

		[MenuItem("Tools/Yan-K/Material Editor")]
		public static void ShowWindow()
		{
			GetWindow<MaterialEditorTool>("Yan-K Material Editor");
		}

		protected override void OnEnable()
		{
			base.OnEnable();
			includeInactive = EditorPrefs.GetBool(IncludeInactivePrefKey, false);
			Undo.undoRedoPerformed += OnUndoRedoPerformed;
			needsRescan = rootObject != null;
		}

		private void OnDisable()
		{
			Undo.undoRedoPerformed -= OnUndoRedoPerformed;
		}

		private void OnUndoRedoPerformed()
		{
			Rescan();
			RefreshUnsavedState();
			Repaint();
		}

		private void OnInspectorUpdate()
		{
			if (!needsRescan) return;
			needsRescan = false;
			Rescan();
			RefreshUnsavedState();
			Repaint();
		}

		private void OnGUI()
		{
			InitStyles();
			if (Event.current.type == EventType.Layout)
			{
				if (needsRescan)
				{
					needsRescan = false;
					Rescan();
				}
				RefreshUnsavedState();
			}
			DrawHeader();

			if (currentTab == 0)
			{
				DrawMaterialControlPanel();
				if (rootObject != null)
					DrawMaterialList();
				else
					DrawEmptyState();
			}
			else
			{
				DrawTextureControlPanel();
				if (rootObject != null)
					DrawTextureList();
				else
					DrawEmptyState();
			}
		}

		protected override void DrawHeader()
		{
			base.DrawHeader();

			// Row 2: Root Object field
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField(L("rootObject", "Root Object"), EditorStyles.boldLabel, GUILayout.Width(80));
			var newRoot = EditorGUILayout.ObjectField(rootObject, typeof(UnityEngine.Object), true);
			if (rootObject != null)
			{
				if (GUILayout.Button("✕", GUILayout.Width(22), GUILayout.Height(18)))
					newRoot = null;
			}
			EditorGUILayout.EndHorizontal();

			if (newRoot != rootObject && (newRoot == null || newRoot is GameObject || newRoot is Material))
			{
				ChangeRoot(newRoot);
				// The change may open a dialog and changes the layout, so end this GUI pass.
				GUIUtility.ExitGUI();
			}
		}

		private void ChangeRoot(UnityEngine.Object newRoot)
		{
			if (!ConfirmUnsavedEdits()) return;

			rootObject = newRoot;
			ClearSession();
			if (rootObject != null) Rescan();
			else ClearAllData();
			RefreshUnsavedState();
		}

		private void Rescan()
		{
			ScanMaterials();
			if (currentTab == 1) ScanTextures();
		}

		// --- Session originals ---
		// What each renderer material slot and texture property held when it was first scanned for the
		// current root. They survive undo/redo, rescans and script reloads, and reset only when the root
		// changes (after the unsaved-edits prompt) or the window closes.

		[System.Serializable]
		private class MaterialOrigin
		{
			public Renderer renderer;
			public int index;
			public Material material;
		}

		[System.Serializable]
		private class TextureOrigin
		{
			public Material material;
			public string property;
			public Texture texture;
		}

		[SerializeField] private List<MaterialOrigin> materialOrigins = new List<MaterialOrigin>();
		[SerializeField] private List<TextureOrigin> textureOrigins = new List<TextureOrigin>();

		private void ClearSession()
		{
			materialOrigins.Clear();
			textureOrigins.Clear();
			inspectedMaterials.Clear();
		}

		private Dictionary<(Renderer, int), Material> BuildMaterialOriginLookup()
		{
			var lookup = new Dictionary<(Renderer, int), Material>();
			foreach (var o in materialOrigins)
				if (o.renderer != null) lookup[(o.renderer, o.index)] = o.material;
			return lookup;
		}

		// Returns the session original for a renderer slot, recording the current material on first sight.
		private Material GetOriginalMaterial(Dictionary<(Renderer, int), Material> lookup, Renderer renderer, int index, Material current)
		{
			if (lookup.TryGetValue((renderer, index), out var original))
			{
				if (original != null) return original;
				materialOrigins.RemoveAll(o => o.renderer == renderer && o.index == index); // Original asset was deleted
			}
			lookup[(renderer, index)] = current;
			materialOrigins.Add(new MaterialOrigin { renderer = renderer, index = index, material = current });
			return current;
		}

		private Dictionary<(Material, string), Texture> BuildTextureOriginLookup()
		{
			var lookup = new Dictionary<(Material, string), Texture>();
			foreach (var o in textureOrigins)
				if (o.material != null) lookup[(o.material, o.property)] = o.texture;
			return lookup;
		}

		// Returns the session original for a material texture property, recording the current texture on first sight.
		private Texture GetOriginalTexture(Dictionary<(Material, string), Texture> lookup, Material material, string property, Texture current)
		{
			if (lookup.TryGetValue((material, property), out var original))
			{
				if (original != null) return original;
				textureOrigins.RemoveAll(o => o.material == material && o.property == property); // Original asset was deleted
			}
			lookup[(material, property)] = current;
			textureOrigins.Add(new TextureOrigin { material = material, property = property, texture = current });
			return current;
		}

		protected override bool HasUnsavedEdits()
		{
			var materialsByRenderer = new Dictionary<Renderer, Material[]>();
			foreach (var o in materialOrigins)
			{
				if (o.renderer == null || o.material == null) continue;
				if (!materialsByRenderer.TryGetValue(o.renderer, out var mats))
					materialsByRenderer[o.renderer] = mats = o.renderer.sharedMaterials;
				if (o.index < mats.Length && mats[o.index] != null && mats[o.index] != o.material) return true;
			}
			foreach (var o in textureOrigins)
			{
				if (o.material == null || o.texture == null || !o.material.HasProperty(o.property)) continue;
				var texture = o.material.GetTexture(o.property);
				if (texture != null && texture != o.texture) return true;
			}
			return false;
		}

		protected override void SaveEdits()
		{
			materialOrigins.Clear();
			textureOrigins.Clear();
			Rescan(); // Records the current values as the new originals.
		}

		protected override void RevertEdits()
		{
			const string undoName = "YME: Revert to Original";
			bool any = false;

			foreach (var group in materialOrigins.Where(o => o.renderer != null && o.material != null).GroupBy(o => o.renderer))
			{
				var renderer = group.Key;
				var mats = renderer.sharedMaterials;
				bool changed = false;
				foreach (var o in group)
				{
					if (o.index >= mats.Length || mats[o.index] == null || mats[o.index] == o.material) continue;
					mats[o.index] = o.material;
					changed = true;
				}
				if (!changed) continue;
				Undo.RecordObject(renderer, undoName);
				renderer.sharedMaterials = mats;
				EditorUtility.SetDirty(renderer);
				any = true;
			}

			foreach (var o in textureOrigins)
			{
				if (o.material == null || o.texture == null || !o.material.HasProperty(o.property)) continue;
				var texture = o.material.GetTexture(o.property);
				if (texture == null || texture == o.texture) continue;
				Undo.RecordObject(o.material, undoName);
				o.material.SetTexture(o.property, o.texture);
				EditorUtility.SetDirty(o.material);
				any = true;
			}

			if (any) Undo.SetCurrentGroupName(undoName);
			Rescan();
		}

		private void DrawTabBar()
		{
			string[] tabNames = {
				L("materialMode", "Material Mode"),
				L("textureMode", "Texture Mode")
			};
			int newTab = GUILayout.Toolbar(currentTab, tabNames, GUILayout.Height(22));
			if (newTab != currentTab)
			{
				currentTab = newTab;
				if (currentTab == 0)
					inspectedMaterials.Clear();
				else
					ScanTextures();
			}
		}

		private void DrawEmptyState()
		{
			DrawCenteredMessage(L("noRootObject", "Drop a GameObject or Material in the Root Object field above to begin."), "d_GameObject Icon");
		}

		// --- Utilities ---

		private void ClearAllData()
		{
			materialSlots.Clear();
			selectAll = false;
			textureSlots.Clear();
			textureSelectAll = false;
			inspectedMaterials.Clear();
		}

		private static string GenerateClonePath(string originalPath)
		{
			string dir = Path.GetDirectoryName(originalPath);
			string baseName = Path.GetFileNameWithoutExtension(originalPath);
			string ext = Path.GetExtension(originalPath);

			// Strip existing (YMEClone N) suffix to avoid stacking
			baseName = System.Text.RegularExpressions.Regex.Replace(baseName, @"\s*\(YMEClone \d+\)$", "");

			int n = 1;
			string newPath;
			do { newPath = Path.Combine(dir, $"{baseName} (YMEClone {n++}){ext}"); }
			while (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(newPath) != null);
			return newPath;
		}
	}
}
