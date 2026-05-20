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
		private Vector2 collectionScroll;
		private Vector2 previewScroll;

		private readonly Dictionary<PackageAssetNode, List<PackageAssetNode>> sortedChildrenCache
			= new Dictionary<PackageAssetNode, List<PackageAssetNode>>();

		private string folderCollectionRootName;
		private readonly List<KeyValuePair<string, string>> previewPairs = new List<KeyValuePair<string, string>>();
		private bool collectionPanelExpanded = false;

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
			if (exporterInitialized) return;
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
			RefreshPreview();
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
				DependencyCollector.Collect(pendingRootPaths);

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

			if (collectionPanelExpanded)
			{
				DrawFolderCollectionPanel();
				return;
			}

			DrawSources();

			GUILayout.Space(4);

			EditorGUILayout.BeginHorizontal();
			DrawTreePanel();
			DrawSidebar();
			EditorGUILayout.EndHorizontal();

			DrawFolderCollectionPanel();
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
			if (DrawColoredToolbarButton(L("yspExport", "Export…"), 90))
				DoExport();

			EditorGUILayout.EndHorizontal();
		}

		private static readonly Color YSP_AccentColor = new Color(0.88f, 0.54f, 0.17f, 1f);

		internal bool DrawColoredToolbarButton(string label, float width)
		{
			Color prev = GUI.backgroundColor;
			GUI.backgroundColor = YSP_AccentColor;
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

		// --- Folder Collection panel ---

		private void DrawFolderCollectionPanel()
		{
			GUILayout.Space(2);
			if (collectionPanelExpanded)
				EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.ExpandHeight(true));
			else
				EditorGUILayout.BeginVertical(EditorStyles.helpBox);

			// Header with big Expand/Collapse icon button (Unity built-in icons, guaranteed to render)
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField(L("yspCollectionPanel", "Folder Collection"), EditorStyles.boldLabel);
			GUILayout.FlexibleSpace();
			GUIContent toggleIcon = collectionPanelExpanded
				? EditorGUIUtility.IconContent("d_winbtn_win_restore")
				: EditorGUIUtility.IconContent("d_winbtn_win_max");
			if (toggleIcon == null || toggleIcon.image == null)
				toggleIcon = new GUIContent(collectionPanelExpanded ? "-" : "+");
			toggleIcon.tooltip = collectionPanelExpanded ? L("yspCollapse", "Collapse") : L("yspExpand", "Expand");
			if (GUILayout.Button(toggleIcon, GUILayout.Width(36), GUILayout.Height(24)))
				collectionPanelExpanded = !collectionPanelExpanded;
			EditorGUILayout.EndHorizontal();

			// Top Mode toolbar (label removed per request)
			string[] topLabels = {
				L("yspTopModeNone", "None"),
				L("yspTopModeNonDestructive", "Non-Destructive"),
				L("yspTopModeDestructive", "Destructive")
			};
			int newTop = GUILayout.Toolbar((int)settings.TopMode, topLabels);
			if (newTop != (int)settings.TopMode)
			{
				settings.TopMode = (FolderCollectionTopMode)newTop;
				settings.Save();
				RefreshPreview();
			}

			// Behaviour toolbar (disabled when TopMode == None)
			EditorGUILayout.LabelField(L("yspFolderCollectionBehaviour", "Behaviour"), EditorStyles.miniBoldLabel);
			string[] behLabels = {
				L("yspCollectionKeepStructure", "Keep Structure"),
				L("yspCollectionAutoOrganize", "Auto Organize"),
				L("yspCollectionSingleFolder", "Single Folder")
			};
			bool behaviourEnabled = settings.TopMode != FolderCollectionTopMode.None;
			using (new EditorGUI.DisabledScope(!behaviourEnabled))
			{
				int newBeh = GUILayout.Toolbar((int)settings.CollectionMode, behLabels);
				if (newBeh != (int)settings.CollectionMode)
				{
					settings.CollectionMode = (FolderCollectionMode)newBeh;
					settings.Save();
					RefreshPreview();
				}
			}

			if (!behaviourEnabled)
			{
				EditorGUILayout.LabelField(L("yspFolderCollectionDisabled", "Top Mode is None — folder collection is disabled."), dimLabelStyle);
				EditorGUILayout.EndVertical();
				return;
			}

			string newRootName = EditorGUILayout.TextField(
				L("yspCollectionRoot", "Root folder name"),
				folderCollectionRootName);
			if (newRootName != folderCollectionRootName)
			{
				folderCollectionRootName = newRootName;
				RefreshPreview();
			}

			EditorGUILayout.BeginHorizontal();
			if (GUILayout.Button(L("yspRefreshPreview", "Refresh Preview"), GUILayout.Width(140)))
				RefreshPreview();
			GUILayout.FlexibleSpace();
			EditorGUILayout.LabelField(
				previewPairs.Count == 0 ? L("yspPreviewEmpty", "(no preview)") : string.Format(L("yspPreviewCount", "{0} entries"), previewPairs.Count),
				dimLabelStyle);
			EditorGUILayout.EndHorizontal();

			if (previewPairs.Count > 0)
			{
				if (collectionPanelExpanded)
					previewScroll = EditorGUILayout.BeginScrollView(previewScroll, GUILayout.ExpandHeight(true));
				else
					previewScroll = EditorGUILayout.BeginScrollView(previewScroll, GUILayout.MaxHeight(140));
				foreach (KeyValuePair<string, string> pair in previewPairs)
				{
					EditorGUILayout.BeginHorizontal();
					EditorGUILayout.SelectableLabel(pair.Key, EditorStyles.miniLabel, GUILayout.Height(14), GUILayout.ExpandWidth(true));
					EditorGUILayout.LabelField("→", GUILayout.Width(14));
					EditorGUILayout.SelectableLabel(pair.Value, EditorStyles.miniLabel, GUILayout.Height(14), GUILayout.ExpandWidth(true));
					EditorGUILayout.EndHorizontal();
				}
				EditorGUILayout.EndScrollView();
			}

			EditorGUILayout.EndVertical();
		}

		private void RefreshPreview()
		{
			previewPairs.Clear();
			if (rootNode == null)
				return;

			if (settings.TopMode == FolderCollectionTopMode.None)
			{
				foreach (PackageAssetNode leaf in EnumerateLeaves(rootNode))
				{
					if (!leaf.IsChecked) continue;
					if (IsLeafExcluded(leaf)) continue;
					previewPairs.Add(new KeyValuePair<string, string>(leaf.FullPath, leaf.FullPath));
				}
				return;
			}

			PathRemapper remapper = new PathRemapper(settings.CollectionMode, folderCollectionRootName);
			foreach (PackageAssetNode leaf in EnumerateLeaves(rootNode))
			{
				if (!leaf.IsChecked) continue;
				if (IsLeafExcluded(leaf)) continue;
				string orig = leaf.FullPath;
				previewPairs.Add(new KeyValuePair<string, string>(orig, remapper.Remap(orig)));
			}
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

			long total = 0;
			foreach (PackageAssetNode l in checkedLeaves)
				total += l.FileSize;

			if (missingDependencies != null && missingDependencies.Length > 0)
			{
				if (!EditorUtility.DisplayDialog(
					L("yspMissingDepTitle", "Missing dependencies"),
					string.Format(L("yspMissingDepMsg", "{0} dependencies are missing. Continue anyway?"), missingDependencies.Length),
					L("yspContinue", "Continue"), L("yspCancel", "Cancel")))
					return;
			}

			string startFolder = EditorPrefs.GetString(Prefs.LastExportFolder, Application.dataPath);
			string outPath = EditorUtility.SaveFilePanel(
				L("yspExportPanelTitle", "Export Smart Package"),
				startFolder, "MyPackage", "unitypackage");
			if (string.IsNullOrEmpty(outPath))
				return;

			List<KeyValuePair<string, string>> mappings = new List<KeyValuePair<string, string>>(checkedLeaves.Count);
			if (settings.TopMode == FolderCollectionTopMode.None)
			{
				foreach (PackageAssetNode leaf in checkedLeaves)
					mappings.Add(new KeyValuePair<string, string>(leaf.FullPath, leaf.FullPath));
			}
			else
			{
				PathRemapper remapper = new PathRemapper(settings.CollectionMode, folderCollectionRootName);
				foreach (PackageAssetNode leaf in checkedLeaves)
					mappings.Add(new KeyValuePair<string, string>(leaf.FullPath, remapper.Remap(leaf.FullPath)));
			}

			if (settings.TopMode == FolderCollectionTopMode.None)
			{
				DoNonDestructiveExport(mappings, outPath);
			}
			else if (settings.TopMode == FolderCollectionTopMode.Destructive)
				DoDestructiveExport(mappings, outPath);
			else
				DoNonDestructiveExport(mappings, outPath);

			EditorPrefs.SetString(Prefs.LastExportFolder, Path.GetDirectoryName(outPath));
			EditorUtility.RevealInFinder(outPath);
		}

		private void DoNonDestructiveExport(List<KeyValuePair<string, string>> mappings, string outPath)
		{
			List<UnityPackageEntry> entries = new List<UnityPackageEntry>(mappings.Count);
			foreach (KeyValuePair<string, string> pair in mappings)
			{
				string orig = pair.Key;
				string remapped = pair.Value;
				string absAsset = Path.GetFullPath(orig);
				string absMeta = absAsset + ".meta";

				if (!File.Exists(absAsset) || !File.Exists(absMeta))
					continue;

				byte[] assetBytes = File.ReadAllBytes(absAsset);
				byte[] metaBytes = File.ReadAllBytes(absMeta);
				string guid = GuidUtility.ExtractGuidFromMeta(metaBytes);
				if (string.IsNullOrEmpty(guid))
					continue;

				entries.Add(new UnityPackageEntry
				{
					Guid = guid,
					AssetPath = remapped,
					AssetBytes = assetBytes,
					MetaBytes = metaBytes
				});
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
			}
			finally
			{
				EditorUtility.ClearProgressBar();
			}
		}

		private void DoDestructiveExport(List<KeyValuePair<string, string>> mappings, string outPath)
		{
			if (!EditorUtility.DisplayDialog(
				L("yspDestructiveConfirmTitle", "Destructive Folder Collection"),
				string.Format(L("yspDestructiveConfirmMsg", "Move {0} assets in project? This cannot be batch-undone."), mappings.Count),
				L("yspContinue", "Continue"), L("yspCancel", "Cancel")))
				return;

			List<string> errors = new List<string>();
			List<KeyValuePair<string, string>> moved = new List<KeyValuePair<string, string>>(mappings.Count);

			AssetDatabase.StartAssetEditing();
			try
			{
				for (int i = 0; i < mappings.Count; i++)
				{
					string orig = mappings[i].Key;
					string remapped = mappings[i].Value;

					EditorUtility.DisplayProgressBar(
						L("yspMoveProgressTitle", "Moving…"),
						string.Format(L("yspMoveProgressMsg", "{0} / {1}"), i + 1, mappings.Count),
						mappings.Count == 0 ? 0f : (float)(i + 1) / mappings.Count);

					string parent = Path.GetDirectoryName(remapped).Replace('\\', '/');
					EnsureAssetFolder(parent);

					string err = AssetDatabase.MoveAsset(orig, remapped);
					if (!string.IsNullOrEmpty(err))
						errors.Add(orig + " → " + remapped + ": " + err);
					else
						moved.Add(new KeyValuePair<string, string>(orig, remapped));
				}
			}
			finally
			{
				AssetDatabase.StopAssetEditing();
				EditorUtility.ClearProgressBar();
			}

			AssetDatabase.SaveAssets();

			string[] remappedPaths = new string[moved.Count];
			for (int i = 0; i < moved.Count; i++)
				remappedPaths[i] = moved[i].Value;

			AssetDatabase.ExportPackage(remappedPaths, outPath,
				ExportPackageOptions.Recurse | ExportPackageOptions.IncludeDependencies);

			if (errors.Count > 0)
			{
				EditorUtility.DisplayDialog(
					L("yspDestructiveResultTitle", "Destructive export completed with errors"),
					string.Format(L("yspDestructiveResultMsg", "Moved {0} / failed {1}.\nFirst error: {2}"),
						moved.Count, errors.Count, errors[0]),
					"OK");
			}
		}

		private static void EnsureAssetFolder(string assetFolderPath)
		{
			if (string.IsNullOrEmpty(assetFolderPath)) return;
			assetFolderPath = assetFolderPath.Replace('\\', '/');
			if (assetFolderPath == "Assets") return;
			if (AssetDatabase.IsValidFolder(assetFolderPath)) return;

			string parent = Path.GetDirectoryName(assetFolderPath).Replace('\\', '/');
			string leaf = Path.GetFileName(assetFolderPath);
			if (!AssetDatabase.IsValidFolder(parent))
				EnsureAssetFolder(parent);
			AssetDatabase.CreateFolder(parent, leaf);
		}
	}
}
