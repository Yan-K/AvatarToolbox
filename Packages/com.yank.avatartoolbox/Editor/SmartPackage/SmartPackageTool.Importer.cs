using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace YanK
{
	public partial class SmartPackageTool
	{
		// --- Importer state ---

		private readonly List<LoadedPackage> loadedPackages = new List<LoadedPackage>();
		private Vector2 importerScroll;
		private ConflictPolicy importerPolicy;
		private bool importerInitialized;

		private void EnsureImporterInit()
		{
			if (importerInitialized) return;
			importerInitialized = true;
			importerPolicy = (ConflictPolicy)EditorPrefs.GetInt(Prefs.ConflictPolicy, (int)ConflictPolicy.Ask);
		}

		private void DrawImporterTab()
		{
			EnsureImporterInit();

			DrawImporterDropZone();
			DrawImporterToolbar();

			GUILayout.Space(4);

			importerScroll = EditorGUILayout.BeginScrollView(importerScroll);
			int removeIndex = -1;
			for (int i = 0; i < loadedPackages.Count; i++)
			{
				if (DrawPackageCard(loadedPackages[i]))
					removeIndex = i;
			}
			EditorGUILayout.EndScrollView();
			if (removeIndex >= 0)
				loadedPackages.RemoveAt(removeIndex);
		}

		private void DrawImporterDropZone()
		{
			Rect rect = GUILayoutUtility.GetRect(0, 56, GUILayout.ExpandWidth(true));
			GUI.Box(rect, L("yspImporterDropZone", "Drag .unitypackage file(s) here"), EditorStyles.helpBox);

			Event evt = Event.current;
			if (!rect.Contains(evt.mousePosition)) return;

			switch (evt.type)
			{
				case EventType.DragUpdated:
					DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
					evt.Use();
					break;
				case EventType.DragPerform:
					DragAndDrop.AcceptDrag();
					foreach (string p in DragAndDrop.paths)
					{
						if (!string.IsNullOrEmpty(p) && p.EndsWith(".unitypackage", System.StringComparison.OrdinalIgnoreCase))
							LoadPackageFromPath(p);
					}
					evt.Use();
					break;
			}
		}

		private void DrawImporterToolbar()
		{
			EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

			if (GUILayout.Button(L("yspAddPackage", "Add Package…"), EditorStyles.toolbarButton, GUILayout.Width(120)))
			{
				string startFolder = EditorPrefs.GetString(Prefs.LastImportFolder, Application.dataPath);
				string picked = EditorUtility.OpenFilePanel(L("yspImporterPanelTitle", "Import .unitypackage"), startFolder, "unitypackage");
				if (!string.IsNullOrEmpty(picked))
				{
					EditorPrefs.SetString(Prefs.LastImportFolder, Path.GetDirectoryName(picked));
					LoadPackageFromPath(picked);
				}
			}

			if (GUILayout.Button(L("yspClearPackages", "Clear"), EditorStyles.toolbarButton, GUILayout.Width(60)))
				loadedPackages.Clear();

			GUILayout.FlexibleSpace();

			GUILayout.Label(L("yspConflictPolicy", "Conflict Policy"), EditorStyles.miniLabel, GUILayout.Width(100));
			ConflictPolicy newPolicy = (ConflictPolicy)EditorGUILayout.EnumPopup(importerPolicy, EditorStyles.toolbarPopup, GUILayout.Width(110));
			if (newPolicy != importerPolicy)
			{
				importerPolicy = newPolicy;
				EditorPrefs.SetInt(Prefs.ConflictPolicy, (int)importerPolicy);
			}

			using (new EditorGUI.DisabledScope(loadedPackages.Count == 0))
			{
				if (DrawColoredToolbarButton(L("yspImport", "Import…"), 90))
					ImportSession.Apply(loadedPackages, importerPolicy);
			}

			EditorGUILayout.EndHorizontal();
		}

		private void LoadPackageFromPath(string path)
		{
			foreach (LoadedPackage existing in loadedPackages)
			{
				if (existing.FilePath == path)
					return;
			}

			List<UnityPackageEntry> entries;
			try
			{
				entries = UnityPackageReader.Read(path);
			}
			catch (System.Exception ex)
			{
				EditorUtility.DisplayDialog("Smart Package Import",
					"Failed to read package:\n" + path + "\n\n" + ex.Message, "OK");
				return;
			}

			IAssetProbe probe = new AssetDatabaseProbe();
			Dictionary<string, ImportConflict> conflictByGuid = ImportConflictResolver.Resolve(entries, probe);

			List<string> paths = new List<string>(entries.Count);
			foreach (UnityPackageEntry e in entries)
			{
				if (!string.IsNullOrEmpty(e.AssetPath))
					paths.Add(e.AssetPath);
			}

			LoadedPackage pkg = new LoadedPackage
			{
				FilePath = path,
				Entries = entries,
				Tree = PackageAssetNode.BuildTree(paths),
				ConflictByGuid = conflictByGuid,
				IsExpanded = true
			};

			loadedPackages.Add(pkg);
		}

		private bool DrawPackageCard(LoadedPackage pkg)
		{
			bool remove = false;

			EditorGUILayout.BeginVertical(EditorStyles.helpBox);

			EditorGUILayout.BeginHorizontal();
			int total = pkg.Entries.Count;
			int selected = CountCheckedLeaves(pkg.Tree);
			string fileName = Path.GetFileName(pkg.FilePath);
			pkg.IsExpanded = EditorGUILayout.Foldout(pkg.IsExpanded,
				string.Format("{0}   ({1} / {2})", fileName, selected, total), true);
			if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22)))
				remove = true;
			EditorGUILayout.EndHorizontal();

			if (pkg.IsExpanded && pkg.Tree != null)
			{
				Dictionary<string, ImportConflict> conflictByPath = BuildConflictByPath(pkg);
				DrawNode(pkg.Tree, 0, conflictByPath);
			}

			EditorGUILayout.EndVertical();
			return remove;
		}

		private static int CountCheckedLeaves(PackageAssetNode root)
		{
			if (root == null) return 0;
			int n = 0;
			foreach (PackageAssetNode _ in root.EnumerateCheckedLeaves())
				n++;
			return n;
		}

		private static Dictionary<string, ImportConflict> BuildConflictByPath(LoadedPackage pkg)
		{
			Dictionary<string, ImportConflict> map = new Dictionary<string, ImportConflict>();
			if (pkg == null || pkg.Entries == null || pkg.ConflictByGuid == null)
				return map;
			foreach (UnityPackageEntry e in pkg.Entries)
			{
				if (e == null || string.IsNullOrEmpty(e.AssetPath))
					continue;
				if (pkg.ConflictByGuid.TryGetValue(e.Guid, out ImportConflict c))
					map[e.AssetPath] = c;
			}
			return map;
		}
	}
}
