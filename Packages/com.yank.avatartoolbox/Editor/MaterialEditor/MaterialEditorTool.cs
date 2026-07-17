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

		private UnityEngine.Object rootObject;
		private Vector2 scrollPosition;
		private bool includeInactive;
		private int currentTab;

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
		}

		private void OnDisable()
		{
			Undo.undoRedoPerformed -= OnUndoRedoPerformed;
		}

		private void OnUndoRedoPerformed()
		{
			ScanMaterials();
			if (currentTab == 1) ScanTextures();
			Repaint();
		}

		private void OnGUI()
		{
			InitStyles();
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
			if (newRoot != rootObject)
			{
				if (newRoot == null || newRoot is GameObject || newRoot is Material)
				{
					rootObject = newRoot;
					inspectedMaterials.Clear();
					if (rootObject != null)
					{
						ScanMaterials();
						if (currentTab == 1) ScanTextures();
					}
					else
					{
						ClearAllData();
					}
				}
			}
			EditorGUILayout.EndHorizontal();
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
