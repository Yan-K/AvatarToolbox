using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace YanK
{
	public partial class SmartPackageTool
	{
		// --- Exporter state ---

		private readonly List<string> pendingRootPaths = new List<string>();
		private PackageAssetNode rootNode;
		private ExporterSettings settings;
		private AssetTypeFilter assetTypeFilter = new AssetTypeFilter();
		private RegexExcluder excludeRegex;
		private HashSet<string> excludeExtSet = new HashSet<string>();
		private string[] missingDependencies = Array.Empty<string>();

		private Vector2 sourcesScroll;
		private Vector2 treeScroll;
		private Vector2 sidebarScroll;
		private Vector2 exportPreviewScroll;

		private readonly Dictionary<PackageAssetNode, List<PackageAssetNode>> sortedChildrenCache
			= new Dictionary<PackageAssetNode, List<PackageAssetNode>>();

		private string folderCollectionRootName;
		private bool exportPreviewDirty = true;
		private readonly List<ExportPreviewItem> exportPreviewItems = new List<ExportPreviewItem>();

		private sealed class ExportPreviewItem
		{
			public string SourcePath;
			public string TargetPath;
		}

		private sealed class ExportPlanItem
		{
			public string SourcePath;
			public string AbsoluteAssetPath;
			public string AbsoluteMetaPath;
			public string TargetPath;
			public string ExpectedGuid;
		}

		// --- Perf caches ---
		private readonly HashSet<PackageAssetNode> visibleCache = new HashSet<PackageAssetNode>();
		private bool visibleCacheValid;
		private bool selectionStatsDirty = true;
		private long cachedSelBytes;
		private int cachedSelCount;
		private int cachedTotalCount;
		private readonly Dictionary<string, int[]> cachedTypeCounts = new Dictionary<string, int[]>();
		private static readonly Dictionary<string, Texture> s_IconCache = new Dictionary<string, Texture>();

		private bool exporterInitialized;


		// --- Init ---

		private void EnsureExporterInit()
		{
			// After a domain reload (e.g. importing assets that contain scripts) Unity can
			// restore the bool init flag as true while leaving non-serialized references
			// like `settings` null. Re-run init in that case so DrawToolbar never
			// dereferences a null settings — mirrors InitStyles' defensive guard.
			if (exporterInitialized && settings != null) return;
			exporterInitialized = true;

			settings = new ExporterSettings();
			settings.Load();
			RebuildMatchers();
			folderCollectionRootName = "YSP_Export_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
		}

		private void RebuildMatchers()
		{
			excludeRegex = new RegexExcluder(settings.ParseRegexPatterns());
			excludeExtSet = new HashSet<string>(settings.ParseExtensions());
			RecomputeExcludeCounts();
			if (rootNode != null)
				assetTypeFilter.Rebuild(EnumerateIncludedLeaves(rootNode));
			InvalidateVisibility();
			MarkSelectionDirty();
		}

		private bool IsLeafExcluded(PackageAssetNode leaf)
		{
			if (leaf == null) return false;
			string ext = string.IsNullOrEmpty(leaf.Extension) ? string.Empty : leaf.Extension.ToLowerInvariant();
			if (excludeExtSet != null && !string.IsNullOrEmpty(ext) && excludeExtSet.Contains(ext))
				return true;
			if (excludeRegex != null && excludeRegex.IsExcluded(leaf.FullPath))
				return true;
			return false;
		}

		private IEnumerable<PackageAssetNode> EnumerateIncludedLeaves(PackageAssetNode node)
		{
			foreach (PackageAssetNode leaf in EnumerateLeaves(node))
				if (!IsLeafExcluded(leaf))
					yield return leaf;
		}

		private void InvalidateVisibility()
		{
			visibleCacheValid = false;
		}

		private void MarkSelectionDirty()
		{
			selectionStatsDirty = true;
			exportPreviewDirty = true;
		}

		private void EnsureSelectionStats()
		{
			if (!selectionStatsDirty) return;
			selectionStatsDirty = false;
			cachedSelBytes = 0;
			cachedSelCount = 0;
			cachedTotalCount = 0;
			cachedTypeCounts.Clear();
			if (rootNode == null) return;
			foreach (PackageAssetNode leaf in EnumerateLeaves(rootNode))
			{
				if (IsLeafExcluded(leaf)) continue;
				cachedTotalCount++;
				if (leaf.IsChecked)
				{
					cachedSelCount++;
					cachedSelBytes += leaf.FileSize;
				}
				string ext = string.IsNullOrEmpty(leaf.Extension) ? "" : leaf.Extension.ToLowerInvariant();
				if (!cachedTypeCounts.TryGetValue(ext, out int[] arr))
				{
					arr = new int[2];
					cachedTypeCounts[ext] = arr;
				}
				arr[0]++;
				if (leaf.IsChecked) arr[1]++;
			}
		}

		private int excludedByExtCount;
		private int excludedByRegexCount;

		private void RecomputeExcludeCounts()
		{
			excludedByExtCount = 0;
			excludedByRegexCount = 0;
			if (rootNode == null) return;
			foreach (PackageAssetNode leaf in EnumerateLeaves(rootNode))
			{
				string ext = string.IsNullOrEmpty(leaf.Extension) ? "" : leaf.Extension.ToLowerInvariant();
				if (!string.IsNullOrEmpty(ext) && excludeExtSet.Contains(ext))
					excludedByExtCount++;
				if (excludeRegex != null && excludeRegex.IsExcluded(leaf.FullPath))
					excludedByRegexCount++;
			}
		}

		private void RebuildSortedCache()
		{
			sortedChildrenCache.Clear();
			if (rootNode == null) return;
			BuildSortedCacheRecursive(rootNode);
		}

		private void BuildSortedCacheRecursive(PackageAssetNode node)
		{
			if (node == null || !node.IsFolder) return;

			List<PackageAssetNode> sorted = new List<PackageAssetNode>(node.Children);
			SortChildrenList(sorted);
			sortedChildrenCache[node] = sorted;

			foreach (PackageAssetNode c in sorted)
				BuildSortedCacheRecursive(c);
		}

		private void SortChildrenList(List<PackageAssetNode> list)
		{
			int dir = settings.Ascending ? 1 : -1;

			Comparison<PackageAssetNode> cmp;
			switch (settings.Sort)
			{
				case ExporterSettings.SortMode.Size:
					cmp = (a, b) =>
					{
						int folderCmp = (b.IsFolder ? 1 : 0) - (a.IsFolder ? 1 : 0);
						if (folderCmp != 0) return folderCmp;
						return dir * a.FileSize.CompareTo(b.FileSize);
					};
					break;
				case ExporterSettings.SortMode.Type:
					cmp = (a, b) =>
					{
						int folderCmp = (b.IsFolder ? 1 : 0) - (a.IsFolder ? 1 : 0);
						if (folderCmp != 0) return folderCmp;
						int ec = string.Compare(a.Extension ?? string.Empty, b.Extension ?? string.Empty, StringComparison.OrdinalIgnoreCase);
						if (ec != 0) return dir * ec;
						return dir * string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
					};
					break;
				default:
					cmp = (a, b) =>
					{
						int folderCmp = (b.IsFolder ? 1 : 0) - (a.IsFolder ? 1 : 0);
						if (folderCmp != 0) return folderCmp;
						return dir * string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
					};
					break;
			}

			list.Sort(cmp);
		}

		private List<PackageAssetNode> GetSortedChildren(PackageAssetNode node)
		{
			if (node == null || !node.IsFolder) return null;
			if (sortedChildrenCache.TryGetValue(node, out List<PackageAssetNode> cached))
				return cached;
			List<PackageAssetNode> sorted = new List<PackageAssetNode>(node.Children);
			SortChildrenList(sorted);
			sortedChildrenCache[node] = sorted;
			return sorted;
		}

		// --- Reload / collect ---

		private void ReloadFromSources()
		{
			DependencyCollector.CollectResult result =
				DependencyCollector.Collect(pendingRootPaths, settings.IncludeDependencies);

			missingDependencies = result.MissingDependencies ?? Array.Empty<string>();
			rootNode = PackageAssetNode.BuildTree(result.AssetPaths ?? Array.Empty<string>());
			rootNode.ComputeSize();
			assetTypeFilter.Rebuild(EnumerateIncludedLeaves(rootNode));
			RecomputeExcludeCounts();
			RebuildSortedCache();
			InvalidateVisibility();
			MarkSelectionDirty();
		}

		private static IEnumerable<PackageAssetNode> EnumerateLeaves(PackageAssetNode node)
		{
			if (node == null) yield break;
			if (!node.IsFolder)
			{
				yield return node;
				yield break;
			}
			foreach (PackageAssetNode c in node.Children)
				foreach (PackageAssetNode l in EnumerateLeaves(c))
					yield return l;
		}

		// --- Tab entry point ---

		private void DrawExporterTab()
		{
			EnsureExporterInit();

			DrawToolbar();

			DrawSources();

			GUILayout.Space(4);

			EditorGUILayout.BeginHorizontal();
			DrawTreePanel();
			DrawSidebar();
			EditorGUILayout.EndHorizontal();

			DrawExportLayoutPanel();

			DrawFooter();
		}

		// --- Sources list (also drop target) ---

		private void DrawSources()
		{
			Rect groupRect = EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.MinHeight(60));

			if (pendingRootPaths.Count == 0)
			{
				EditorGUILayout.LabelField(L("yspDropZone", "Drag assets / folders here"), centeredMessageStyle, GUILayout.MinHeight(50));
			}
			else
			{
				sourcesScroll = EditorGUILayout.BeginScrollView(sourcesScroll, GUILayout.MaxHeight(120));
				int removeIndex = -1;
				for (int i = 0; i < pendingRootPaths.Count; i++)
				{
					EditorGUILayout.BeginHorizontal();
					if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22)))
						removeIndex = i;
					EditorGUILayout.LabelField(pendingRootPaths[i], EditorStyles.miniLabel);
					EditorGUILayout.EndHorizontal();
				}
				EditorGUILayout.EndScrollView();
				if (removeIndex >= 0)
				{
					pendingRootPaths.RemoveAt(removeIndex);
					ReloadFromSources();
				}
			}

			EditorGUILayout.EndVertical();

			HandleSourcesDrop(groupRect);
		}

		private void HandleSourcesDrop(Rect rect)
		{
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
					bool added = false;
					foreach (UnityEngine.Object obj in DragAndDrop.objectReferences)
					{
						string p = AssetDatabase.GetAssetPath(obj);
						if (!string.IsNullOrEmpty(p) && !pendingRootPaths.Contains(p))
						{
							pendingRootPaths.Add(p);
							added = true;
						}
					}
					if (added)
						ReloadFromSources();
					evt.Use();
					break;
			}
		}

		// --- Toolbar ---

		private void DrawToolbar()
		{
			EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

			if (GUILayout.Button(L("yspAddSelection", "Add Selection"), EditorStyles.toolbarButton, GUILayout.Width(110)))
			{
				bool added = false;
				foreach (UnityEngine.Object obj in Selection.objects)
				{
					string p = AssetDatabase.GetAssetPath(obj);
					if (!string.IsNullOrEmpty(p) && !pendingRootPaths.Contains(p))
					{
						pendingRootPaths.Add(p);
						added = true;
					}
				}
				if (added) ReloadFromSources();
			}
			if (GUILayout.Button(L("yspClearSources", "Clear"), EditorStyles.toolbarButton, GUILayout.Width(60)))
			{
				pendingRootPaths.Clear();
				ReloadFromSources();
			}

			GUILayout.FlexibleSpace();

			if (GUILayout.Button(L("yspReload", "Reload"), EditorStyles.toolbarButton, GUILayout.Width(80)))
				ReloadFromSources();
			if (DrawColoredToolbarButton(L("yspExport", "Export…"), 90, YSP_AccentColor))
				DoExport();

			EditorGUILayout.EndHorizontal();
		}

		private static readonly Color YSP_AccentColor = new Color(0.88f, 0.54f, 0.17f, 1f);

		internal bool DrawColoredToolbarButton(string label, float width)
		{
			return DrawColoredToolbarButton(label, width, YSP_AccentColor);
		}

		internal bool DrawColoredToolbarButton(string label, float width, Color color)
		{
			Color prev = GUI.backgroundColor;
			GUI.backgroundColor = color;
			bool clicked = GUILayout.Button(label, EditorStyles.toolbarButton, GUILayout.Width(width));
			GUI.backgroundColor = prev;
			return clicked;
		}

		private void SetAllLeaves(bool value)
		{
			if (rootNode == null) return;
			foreach (PackageAssetNode leaf in EnumerateLeaves(rootNode))
				leaf.SetChecked(value, false);
			RecomputeAllFromLeaves(rootNode);
			MarkSelectionDirty();
		}

		private void RecomputeAllFromLeaves(PackageAssetNode node)
		{
			if (node == null || !node.IsFolder) return;
			foreach (PackageAssetNode c in node.Children)
				RecomputeAllFromLeaves(c);
			node.RecomputeFromChildren();
		}

		// --- Tree panel ---

		private void DrawTreePanel()
		{
			EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

			if (rootNode == null || rootNode.Children.Count == 0)
			{
				EditorGUILayout.LabelField(L("yspTreeEmpty", "Drop assets above and click Reload."), centeredMessageStyle, GUILayout.MinHeight(160));
				EditorGUILayout.EndVertical();
				return;
			}

			treeScroll = EditorGUILayout.BeginScrollView(treeScroll);
			DrawNode(rootNode, 0);
			EditorGUILayout.EndScrollView();

			EditorGUILayout.EndVertical();
		}

		// --- Sidebar ---

		private void DrawSidebar()
		{
			EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Width(250));
			sidebarScroll = EditorGUILayout.BeginScrollView(sidebarScroll);

			// Select all / Deselect all (short labels to fit narrow sidebar)
			EditorGUILayout.BeginHorizontal();
			if (GUILayout.Button(L("yspSelectAllShort", "All"), EditorStyles.miniButtonLeft))
				SetAllLeaves(true);
			if (GUILayout.Button(L("yspDeselectAllShort", "None"), EditorStyles.miniButtonRight))
				SetAllLeaves(false);
			EditorGUILayout.EndHorizontal();

			GUILayout.Space(6);

			// Include Dependencies — toggling re-collects, so reload the tree immediately.
			bool newIncludeDeps = EditorGUILayout.ToggleLeft(
				new GUIContent(
					L("yspIncludeDependencies", "Include Dependencies"),
					L("yspIncludeDependenciesTip", "When on, automatically collect all assets referenced by your selection. When off, export only the assets you added.")),
				settings.IncludeDependencies);
			if (newIncludeDeps != settings.IncludeDependencies)
			{
				settings.IncludeDependencies = newIncludeDeps;
				settings.Save();
				ReloadFromSources();
			}

			// Search
			EditorGUILayout.LabelField(L("yspSearch", "Search"), EditorStyles.boldLabel);
			string newSearch = EditorGUILayout.TextField(settings.SearchText, searchFieldStyle);
			if (newSearch != settings.SearchText)
			{
				settings.SearchText = newSearch;
				InvalidateVisibility();
			}

			GUILayout.Space(6);

			// Sort
			EditorGUILayout.LabelField(L("yspSort", "Sort"), EditorStyles.boldLabel);
			string[] sortLabels = {
				L("yspSortName", "Name"),
				L("yspSortSize", "Size"),
				L("yspSortType", "Type")
			};
			ExporterSettings.SortMode newSort = (ExporterSettings.SortMode)EditorGUILayout.Popup((int)settings.Sort, sortLabels);
			bool newAsc = EditorGUILayout.ToggleLeft(L("yspSortAscending", "Ascending"), settings.Ascending);
			if (newSort != settings.Sort || newAsc != settings.Ascending)
			{
				settings.Sort = newSort;
				settings.Ascending = newAsc;
				settings.Save();
				RebuildSortedCache();
			}

			GUILayout.Space(6);

			// Exclude Extensions (hides matching leaves)
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField(L("yspExcludeExt", "Exclude Extensions"), EditorStyles.boldLabel);
			if (GUILayout.Button("↺", EditorStyles.miniButton, GUILayout.Width(22)))
			{
				settings.ExcludeExtensionsRaw = ".cs,.shader";
				settings.Save();
				RebuildMatchers();
			}
			EditorGUILayout.EndHorizontal();
			string newExt = EditorGUILayout.TextField(settings.ExcludeExtensionsRaw);
			if (newExt != settings.ExcludeExtensionsRaw)
			{
				settings.ExcludeExtensionsRaw = newExt;
				settings.Save();
				RebuildMatchers();
			}
			EditorGUILayout.LabelField(string.Format(L("yspExcludedCount", "(excluded {0})"), excludedByExtCount), dimLabelStyle);

			GUILayout.Space(6);

			// Exclude Names (Regex)
			EditorGUILayout.LabelField(L("yspExcludeNamesRegex", "Exclude Names (Regex)"), EditorStyles.boldLabel);
			string newNames = EditorGUILayout.TextField(settings.ExcludeNamesRaw);
			if (newNames != settings.ExcludeNamesRaw)
			{
				settings.ExcludeNamesRaw = newNames;
				settings.Save();
				RebuildMatchers();
			}
			EditorGUILayout.LabelField(string.Format(L("yspExcludedCount", "(excluded {0})"), excludedByRegexCount), dimLabelStyle);

			GUILayout.Space(6);

			// Type Filter (tri-state, uses cached counts)
			EditorGUILayout.LabelField(L("yspTypeFilter", "Type Filter"), EditorStyles.boldLabel);
			EnsureSelectionStats();
			if (assetTypeFilter.Counts.Count == 0)
			{
				EditorGUILayout.LabelField(L("yspNoTypes", "(no types)"), dimLabelStyle);
			}
			else
			{
				List<string> keys = new List<string>(assetTypeFilter.Counts.Keys);
				keys.Sort(StringComparer.OrdinalIgnoreCase);
				foreach (string ext in keys)
					DrawTypeFilterTriState(ext);
			}

			EditorGUILayout.EndScrollView();
			EditorGUILayout.EndVertical();
		}

		private void DrawExportLayoutPanel()
		{
			GUILayout.Space(4);
			EditorGUILayout.BeginVertical(EditorStyles.helpBox);
			EditorGUILayout.LabelField(L("yspExportLayout", "Export Layout"), EditorStyles.boldLabel);
			string[] layoutLabels = {
				L("yspCollectionKeepStructure", "Keep Structure"),
				L("yspCollectionAutoOrganize", "Auto Organize"),
				L("yspCollectionSingleFolder", "Single Folder")
			};
			FolderCollectionMode newMode = (FolderCollectionMode)GUILayout.Toolbar(
				(int)settings.CollectionMode, layoutLabels, GUILayout.Height(22f));
			if (newMode != settings.CollectionMode)
			{
				settings.CollectionMode = newMode;
				settings.Save();
				exportPreviewDirty = true;
			}

			if (settings.CollectionMode != FolderCollectionMode.KeepStructure)
			{
				GUILayout.Space(4);
				EditorGUILayout.BeginHorizontal();
				EditorGUILayout.LabelField(L("yspCollectionRoot", "Root folder name"), GUILayout.Width(130f));
				string newRootName = EditorGUILayout.TextField(folderCollectionRootName);
				if (newRootName != folderCollectionRootName)
				{
					folderCollectionRootName = newRootName;
					exportPreviewDirty = true;
				}
				if (GUILayout.Button(L("yspRefreshPreview", "Refresh Preview"), GUILayout.Width(120f)))
					exportPreviewDirty = true;
				EditorGUILayout.EndHorizontal();

				EnsureExportPreview();
				EditorGUILayout.LabelField(
					string.Format(L("yspPreviewCount", "{0} entries"), exportPreviewItems.Count),
					EditorStyles.miniLabel);

				exportPreviewScroll = EditorGUILayout.BeginScrollView(
					exportPreviewScroll,
					EditorStyles.helpBox,
					GUILayout.MinHeight(70f),
					GUILayout.MaxHeight(140f));
				if (exportPreviewItems.Count == 0)
				{
					EditorGUILayout.LabelField(L("yspPreviewEmpty", "(no preview)"), centeredMessageStyle, GUILayout.MinHeight(48f));
				}
				else
				{
					for (int i = 0; i < exportPreviewItems.Count; i++)
					{
						ExportPreviewItem item = exportPreviewItems[i];
						EditorGUILayout.SelectableLabel(
							item.SourcePath + "  →  " + item.TargetPath,
							EditorStyles.miniLabel,
							GUILayout.Height(16f));
					}
				}
				EditorGUILayout.EndScrollView();
			}

			EditorGUILayout.EndVertical();
		}

		private void EnsureExportPreview()
		{
			if (!exportPreviewDirty)
				return;

			exportPreviewDirty = false;
			exportPreviewItems.Clear();
			if (rootNode == null || settings.CollectionMode == FolderCollectionMode.KeepStructure)
				return;

			string effectiveRootName = string.IsNullOrWhiteSpace(folderCollectionRootName)
				? "YSP_Export"
				: folderCollectionRootName.Trim();
			PathRemapper remapper = new PathRemapper(settings.CollectionMode, effectiveRootName);
			foreach (PackageAssetNode leaf in rootNode.EnumerateCheckedLeaves())
			{
				if (IsLeafExcluded(leaf))
					continue;
				exportPreviewItems.Add(new ExportPreviewItem
				{
					SourcePath = leaf.FullPath,
					TargetPath = remapper.Remap(leaf.FullPath)
				});
			}
		}

		private void DrawTypeFilterTriState(string ext)
		{
			int total = 0, checkedCount = 0;
			if (cachedTypeCounts.TryGetValue(ext, out int[] arr))
			{
				total = arr[0];
				checkedCount = arr[1];
			}

			bool all = total > 0 && checkedCount == total;
			bool mixed = checkedCount > 0 && checkedCount < total;

			EditorGUILayout.BeginHorizontal();
			bool prevMixed = EditorGUI.showMixedValue;
			EditorGUI.showMixedValue = mixed;
			bool nv = EditorGUILayout.Toggle(all, GUILayout.Width(16));
			EditorGUI.showMixedValue = prevMixed;
			string label = (string.IsNullOrEmpty(ext) ? "(none)" : ext) + "  (" + checkedCount + " / " + total + ")";
			GUILayout.Label(label);
			EditorGUILayout.EndHorizontal();

			if (nv != all)
			{
				bool value = nv;
				foreach (PackageAssetNode leaf in EnumerateLeaves(rootNode))
				{
					string le = string.IsNullOrEmpty(leaf.Extension) ? "" : leaf.Extension.ToLowerInvariant();
					if (le == ext) leaf.IsChecked = value;
				}
				if (rootNode != null) rootNode.RecomputeFromChildren();
				MarkSelectionDirty();
			}
		}

		// --- Footer ---

		private void DrawFooter()
		{
			GUILayout.Space(2);
			EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

			EnsureSelectionStats();
			float megabytes = cachedSelBytes / (1024f * 1024f);
			string sizeText = megabytes.ToString("0.0") + " MB";
			string footer = string.Format(
				L("yspFooterStats", "Selected {0} / {1} — {2}"),
				cachedSelCount, cachedTotalCount, sizeText);

			EditorGUILayout.LabelField(footer);

			GUILayout.FlexibleSpace();

			if (missingDependencies != null && missingDependencies.Length > 0)
			{
				Color p2 = GUI.contentColor;
				GUI.contentColor = Color.red;
				EditorGUILayout.LabelField(string.Format(L("yspMissingDepCount", "Missing: {0}"), missingDependencies.Length),
					GUILayout.Width(120));
				GUI.contentColor = p2;
			}

			EditorGUILayout.EndHorizontal();
		}

		// --- Export ---

		private void DoExport()
		{
			if (rootNode == null)
			{
				EditorUtility.DisplayDialog(L("yspExportEmptyTitle", "Nothing to export"),
					L("yspExportEmptyMsg", "Add sources and click Reload first."), "OK");
				return;
			}

			List<PackageAssetNode> checkedLeaves = new List<PackageAssetNode>();
			foreach (PackageAssetNode leaf in rootNode.EnumerateCheckedLeaves())
				if (!IsLeafExcluded(leaf))
					checkedLeaves.Add(leaf);
			if (checkedLeaves.Count == 0)
			{
				EditorUtility.DisplayDialog(L("yspExportEmptyTitle", "Nothing to export"),
					L("yspExportNoSelectionMsg", "No assets selected."), "OK");
				return;
			}

			if (!TryBuildExportPlan(checkedLeaves, out List<ExportPlanItem> exportPlan, out string preflightError))
			{
				EditorUtility.DisplayDialog(
					L("yspExportPreflightTitle", "Export preflight failed"),
					preflightError,
					"OK");
				return;
			}

			if (missingDependencies != null && missingDependencies.Length > 0)
			{
				if (!EditorUtility.DisplayDialog(
					L("yspMissingDepTitle", "Missing dependencies"),
					string.Format(L("yspMissingDepMsg", "{0} dependencies are missing. Continue anyway?"), missingDependencies.Length),
					L("yspContinue", "Continue"), L("yspCancel", "Cancel")))
					return;
			}

			string startFolder = EditorPrefs.GetString(Prefs.LastExportFolder, Application.dataPath);
			string defaultName = settings.CollectionMode == FolderCollectionMode.KeepStructure
				? "YSP_Export_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss")
				: string.IsNullOrWhiteSpace(folderCollectionRootName)
					? "YSP_Export_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss")
					: folderCollectionRootName.Trim();
			string outPath = EditorUtility.SaveFilePanel(
				L("yspExportPanelTitle", "Export Smart Package"),
				startFolder, defaultName, "unitypackage");
			if (string.IsNullOrEmpty(outPath))
				return;

			if (!DoNonDestructiveExport(exportPlan, outPath))
				return;

			EditorPrefs.SetString(Prefs.LastExportFolder, Path.GetDirectoryName(outPath));
			EditorUtility.RevealInFinder(outPath);
		}

		private bool TryBuildExportPlan(
			List<PackageAssetNode> checkedLeaves,
			out List<ExportPlanItem> exportPlan,
			out string error)
		{
			exportPlan = new List<ExportPlanItem>(checkedLeaves.Count);
			error = null;

			string effectiveRootName = string.IsNullOrWhiteSpace(folderCollectionRootName)
				? "YSP_Export"
				: folderCollectionRootName.Trim();
			if (settings.CollectionMode != FolderCollectionMode.KeepStructure
				&& (effectiveRootName.IndexOf('/') >= 0 || effectiveRootName.IndexOf('\\') >= 0))
			{
				error = "The export root must be one folder name, not a nested path.";
				return false;
			}
			if (settings.CollectionMode != FolderCollectionMode.KeepStructure)
			{
				string rootProbe = "Assets/" + effectiveRootName + "/__YSP_ROOT_VALIDATION__";
				if (!ProjectPackagePath.TryNormalize(rootProbe, out _, out string rootError))
				{
					error = "Invalid export root folder.\n\n" + rootError;
					return false;
				}
			}

			string projectRoot = Path.GetDirectoryName(Application.dataPath);
			PathRemapper remapper = new PathRemapper(settings.CollectionMode, effectiveRootName);
			HashSet<string> targetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, string> sourceByGuid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			List<string> errors = new List<string>();

			foreach (PackageAssetNode leaf in checkedLeaves)
			{
				string sourcePath = leaf.FullPath;
				string remapped = remapper.Remap(sourcePath);
				if (!ProjectPackagePath.TryNormalize(remapped, out string normalizedTarget, out string targetError))
				{
					errors.Add(sourcePath + ": invalid export path. " + targetError);
					continue;
				}
				if (!ProjectPackagePath.TryGetAbsolutePath(
					projectRoot,
					normalizedTarget,
					out _,
					out string targetAbsoluteError))
				{
					errors.Add(sourcePath + ": invalid export target. " + targetAbsoluteError);
					continue;
				}
				if (!targetPaths.Add(normalizedTarget))
				{
					errors.Add(sourcePath + ": duplicate export path " + normalizedTarget);
					continue;
				}

				if (!ProjectPackagePath.TryGetAbsolutePath(
					projectRoot,
					sourcePath,
					out string absAsset,
					out string sourceError))
				{
					errors.Add(sourcePath + ": " + sourceError);
					continue;
				}

				string absMeta = absAsset + ".meta";
				if (!File.Exists(absAsset))
				{
					errors.Add(sourcePath + ": asset file is missing.");
					continue;
				}
				if (!File.Exists(absMeta))
				{
					errors.Add(sourcePath + ": meta file is missing.");
					continue;
				}

				string guid;
				try
				{
					guid = GuidUtility.ExtractGuidFromMeta(File.ReadAllBytes(absMeta));
				}
				catch (Exception ex)
				{
					errors.Add(sourcePath + ": could not read its meta file. " + ex.Message);
					continue;
				}
				if (!GuidUtility.IsValidGuid(guid))
				{
					errors.Add(sourcePath + ": meta file has no valid GUID.");
					continue;
				}
				if (sourceByGuid.TryGetValue(guid, out string firstGuidSource))
				{
					errors.Add(sourcePath + ": GUID " + guid + " is also used by " + firstGuidSource + ".");
					continue;
				}
				sourceByGuid[guid] = sourcePath;

				exportPlan.Add(new ExportPlanItem
				{
					SourcePath = sourcePath,
					AbsoluteAssetPath = absAsset,
					AbsoluteMetaPath = absMeta,
					TargetPath = normalizedTarget,
					ExpectedGuid = guid
				});
			}

			if (errors.Count == 0)
				return true;

			const int maxDisplayedErrors = 12;
			IEnumerable<string> displayedErrors = errors
				.Take(maxDisplayedErrors)
				.Select(item => "- " + item);
			error = "Fix the following problems before exporting:\n\n"
				+ string.Join("\n", displayedErrors.ToArray());
			if (errors.Count > maxDisplayedErrors)
				error += "\n- …and " + (errors.Count - maxDisplayedErrors) + " more.";
			return false;
		}

		private bool DoNonDestructiveExport(List<ExportPlanItem> exportPlan, string outPath)
		{
			if (!TryLoadExportEntries(exportPlan, out List<UnityPackageEntry> entries, out string preflightError))
			{
				EditorUtility.DisplayDialog(
					L("yspExportPreflightTitle", "Export preflight failed"),
					preflightError,
					"OK");
				return false;
			}

			try
			{
				UnityPackageWriter.Write(outPath, entries, (current, max) =>
				{
					EditorUtility.DisplayProgressBar(
						L("yspExportProgressTitle", "Exporting…"),
						string.Format(L("yspExportProgressMsg", "Writing {0} / {1}"), current, max),
						max == 0 ? 0f : (float)current / max);
				});
				return true;
			}
			catch (Exception ex)
			{
				EditorUtility.DisplayDialog(
					L("yspExportFailedTitle", "Export failed"),
					ex.Message,
					"OK");
				return false;
			}
			finally
			{
				EditorUtility.ClearProgressBar();
			}
		}

		private bool TryLoadExportEntries(
			List<ExportPlanItem> exportPlan,
			out List<UnityPackageEntry> entries,
			out string error)
		{
			entries = new List<UnityPackageEntry>(exportPlan.Count);
			error = null;
			string projectRoot = Path.GetDirectoryName(Application.dataPath);

			for (int i = 0; i < exportPlan.Count; i++)
			{
				ExportPlanItem item = exportPlan[i];
				if (!ProjectPackagePath.TryNormalize(item.TargetPath, out string normalizedTarget, out string targetError))
				{
					error = item.SourcePath + ": invalid export path. " + targetError;
					return false;
				}

				if (!ProjectPackagePath.TryGetAbsolutePath(
					projectRoot,
					item.SourcePath,
					out string currentAssetPath,
					out string sourceError))
				{
					error = item.SourcePath + ": " + sourceError;
					return false;
				}
				string currentMetaPath = currentAssetPath + ".meta";
				if (!string.Equals(currentAssetPath, item.AbsoluteAssetPath, StringComparison.OrdinalIgnoreCase)
					|| !string.Equals(currentMetaPath, item.AbsoluteMetaPath, StringComparison.OrdinalIgnoreCase))
				{
					error = item.SourcePath + ": source path changed after preflight.";
					return false;
				}
				if (!File.Exists(currentAssetPath))
				{
					error = item.SourcePath + ": asset file is missing.";
					return false;
				}
				if (!File.Exists(currentMetaPath))
				{
					error = item.SourcePath + ": meta file is missing.";
					return false;
				}

				byte[] assetBytes;
				byte[] metaBytes;
				try
				{
					assetBytes = File.ReadAllBytes(currentAssetPath);
					metaBytes = File.ReadAllBytes(currentMetaPath);
				}
				catch (Exception ex)
				{
					error = item.SourcePath + ": could not read the asset or meta file. " + ex.Message;
					return false;
				}

				string guid = GuidUtility.ExtractGuidFromMeta(metaBytes);
				if (!GuidUtility.IsValidGuid(guid))
				{
					error = item.SourcePath + ": meta file has no valid GUID.";
					return false;
				}
				if (!string.Equals(guid, item.ExpectedGuid, StringComparison.OrdinalIgnoreCase))
				{
					error = item.SourcePath + ": meta GUID changed after preflight.";
					return false;
				}

				entries.Add(new UnityPackageEntry
				{
					Guid = guid,
					MetaGuid = guid,
					AssetPath = normalizedTarget,
					AssetBytes = assetBytes,
					MetaBytes = metaBytes,
					EntryOrder = i,
					HasAssetMember = true,
					HasMetaMember = true,
					HasPathnameMember = true,
					Size = assetBytes.LongLength
				});
			}

			return true;
		}
	}
}
