using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace YanK
{
	public partial class SmartPackageTool
	{
		private const float RowHeight = 18f;
		private const float TreeIndentWidth = 14f;
		private const float TreeFoldoutWidth = 14f;
		private const float TreeToggleWidth = 16f;
		private const float TreeIconWidth = 18f;
		private const float TreeSizeWidth = 70f;

		private static readonly Color NewColor = new Color(0.30f, 0.70f, 0.30f, 0.85f);
		private static readonly Color IdenticalColor = new Color(0.25f, 0.50f, 0.90f, 0.85f);
		private static readonly Color UpdateColor = new Color(0.80f, 0.52f, 0.10f, 0.90f);
		private static readonly Color ConflictColor = new Color(0.85f, 0.20f, 0.20f, 0.90f);

		private static GUIStyle s_RowName;
		private static GUIStyle s_ImporterRowName;
		private static GUIStyle s_RowSize;

		// Importer trees draw only the rows inside the scroll view, from a flat list rebuilt on Layout.
		private struct FlatRow
		{
			public PackageAssetNode Node;
			public int Depth;
		}

		private readonly Dictionary<PackageAssetNode, List<FlatRow>> flatTrees = new Dictionary<PackageAssetNode, List<FlatRow>>();
		private readonly Dictionary<PackageAssetNode, float> treeTops = new Dictionary<PackageAssetNode, float>();
		// Every importer row in display order, including rows outside the view, for the arrow keys.
		private readonly List<PackageAssetNode> visibleRows = new List<PackageAssetNode>();
		private readonly Dictionary<PackageAssetNode, string> treeControlNames = new Dictionary<PackageAssetNode, string>();
		private readonly Dictionary<PackageAssetNode, PackageAssetNode.ToggleState> treeStates = new Dictionary<PackageAssetNode, PackageAssetNode.ToggleState>();
		private readonly Dictionary<ImportConflictKind, float> badgeWidths = new Dictionary<ImportConflictKind, float>();
		private int treeStateVersion = -1;
		private string badgeLanguage;
		private bool treeKeyboardFocus;

		private static GUIStyle RowNameStyle
		{
			get
			{
				if (s_RowName == null)
					s_RowName = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleLeft };
				return s_RowName;
			}
		}

		private static GUIStyle ImporterRowNameStyle
		{
			get
			{
				if (s_ImporterRowName == null)
					s_ImporterRowName = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
				return s_ImporterRowName;
			}
		}

		private static GUIStyle RowSizeStyle
		{
			get
			{
				if (s_RowSize == null)
				{
					s_RowSize = new GUIStyle(EditorStyles.miniLabel)
					{
						alignment = TextAnchor.MiddleRight
					};
				}
				return s_RowSize;
			}
		}

		// A folder's checkbox state, computed once per selection change instead of walking its
		// subtree for every drawn row on every event.
		private PackageAssetNode.ToggleState GetTreeState(PackageAssetNode node)
		{
			if (treeStateVersion != importerCheckVersion)
			{
				treeStates.Clear();
				treeStateVersion = importerCheckVersion;
			}
			if (treeStates.TryGetValue(node, out PackageAssetNode.ToggleState state))
				return state;
			state = node.IsChecked ? PackageAssetNode.ToggleState.Checked : PackageAssetNode.ToggleState.Unchecked;
			if (node.IsFolder && node.Children.Count > 0)
			{
				state = GetTreeState(node.Children[0]);
				for (int i = 1; i < node.Children.Count && state != PackageAssetNode.ToggleState.Mixed; i++)
					if (GetTreeState(node.Children[i]) != state)
						state = PackageAssetNode.ToggleState.Mixed;
			}
			treeStates[node] = state;
			return state;
		}

		// Drops what the rows of a removed package (or of every package) kept for drawing.
		private void ForgetTreeRows(PackageAssetNode root = null)
		{
			if (root == null)
			{
				flatTrees.Clear();
				treeTops.Clear();
			}
			else
			{
				flatTrees.Remove(root);
				treeTops.Remove(root);
			}
			treeControlNames.Clear();
			treeStates.Clear();
			treeStateVersion = -1;
			treeKeyboardFocus = false;
		}

		private string TreeControlName(PackageAssetNode node)
		{
			if (!treeControlNames.TryGetValue(node, out string name))
			{
				name = "YSPImportTree:" + treeControlNames.Count;
				treeControlNames.Add(node, name);
			}
			return name;
		}

		// Arrow keys move the highlight, Left and Right fold, Space toggles. Runs before the
		// package list draws, using the row order the previous event drew.
		private void BeginTreeGUI()
		{
			Event current = Event.current;
			// A highlighted row scrolled out of view is not drawn, so its focused control has no name.
			string focused = GUI.GetNameOfFocusedControl();
			// A highlighted row hidden by a fold takes no keys.
			int index = importerHighlightNode == null ? -1 : visibleRows.IndexOf(importerHighlightNode);
			if (treeKeyboardFocus && index >= 0 && current.type == EventType.KeyDown
				&& (focused.Length == 0 || focused.StartsWith("YSPImportTree:", StringComparison.Ordinal))
				&& !EditorGUIUtility.editingTextField)
			{
				bool handled = true;
				switch (current.keyCode)
				{
					case KeyCode.UpArrow:
						if (index > 0)
							importerHighlightNode = visibleRows[index - 1];
						RevealHighlight();
						break;
					case KeyCode.DownArrow:
						if (index >= 0 && index + 1 < visibleRows.Count)
							importerHighlightNode = visibleRows[index + 1];
						RevealHighlight();
						break;
					case KeyCode.LeftArrow:
						if (importerHighlightNode.IsFolder && importerHighlightNode.IsExpanded && importerHighlightNode.Children.Count > 0)
							importerHighlightNode.IsExpanded = false;
						else if (importerHighlightNode.Parent != null && !string.IsNullOrEmpty(importerHighlightNode.Parent.FullPath))
							importerHighlightNode = importerHighlightNode.Parent;
						RevealHighlight();
						break;
					case KeyCode.RightArrow:
						if (importerHighlightNode.IsFolder)
							importerHighlightNode.IsExpanded = true;
						break;
					case KeyCode.Space:
						importerHighlightNode.SetChecked(GetTreeState(importerHighlightNode) != PackageAssetNode.ToggleState.Checked, true);
						RefreshImporterConflictPreview(false);
						break;
					default:
						handled = false;
						break;
				}
				if (handled)
				{
					current.Use();
					Repaint();
				}
			}
			visibleRows.Clear();
		}

		private void RevealHighlight()
		{
			foreach (KeyValuePair<PackageAssetNode, List<FlatRow>> tree in flatTrees)
			{
				int index = tree.Value.FindIndex(row => row.Node == importerHighlightNode);
				if (index < 0 || !treeTops.TryGetValue(tree.Key, out float top))
					continue;
				float y = top + index * RowHeight;
				if (y < importerScroll.y)
					importerScroll.y = y;
				else if (y + RowHeight > importerScroll.y + importerViewHeight)
					importerScroll.y = y + RowHeight - importerViewHeight;
				return;
			}
		}

		private void HighlightRow(PackageAssetNode node)
		{
			importerHighlightNode = node;
			treeKeyboardFocus = true;
			GUI.FocusControl(TreeControlName(node));
			Repaint();
		}

		private void DrawVirtualTree(PackageAssetNode root, Dictionary<string, ImportConflict> conflicts)
		{
			if (!flatTrees.TryGetValue(root, out List<FlatRow> rows) || Event.current.type == EventType.Layout)
			{
				if (rows == null)
					flatTrees[root] = rows = new List<FlatRow>();
				rows.Clear();
				Flatten(root, 0, rows);
			}
			foreach (FlatRow row in rows)
				visibleRows.Add(row.Node);
			Rect block = GUILayoutUtility.GetRect(0f, rows.Count * RowHeight, GUILayout.ExpandWidth(true));
			if (Event.current.type == EventType.Repaint)
				treeTops[root] = block.y;
			// The last repaint's position keeps the drawn slice identical across a frame's events.
			float top = treeTops.TryGetValue(root, out float known) ? known : 0f;
			int first = Mathf.Max(0, Mathf.FloorToInt((importerScroll.y - top) / RowHeight) - 1);
			int last = Mathf.Min(rows.Count, Mathf.CeilToInt((importerScroll.y + importerViewHeight - top) / RowHeight) + 1);
			for (int i = first; i < last; i++)
				DrawNodeRow(rows[i].Node, rows[i].Depth, conflicts, new Rect(block.x, block.y + i * RowHeight, block.width, RowHeight));
		}

		private static void Flatten(PackageAssetNode node, int depth, List<FlatRow> rows)
		{
			// BuildProjectTree uses a synthetic container so project-root files and all
			// real top-level folders can share one tree. It is not part of the package.
			if (node.Parent == null && string.IsNullOrEmpty(node.FullPath))
			{
				foreach (PackageAssetNode topLevel in node.Children)
					Flatten(topLevel, depth, rows);
				return;
			}
			rows.Add(new FlatRow { Node = node, Depth = depth });
			if (node.IsFolder && node.IsExpanded)
				foreach (PackageAssetNode child in node.Children)
					Flatten(child, depth + 1, rows);
		}

		private Rect DrawRowCore(PackageAssetNode node, int depth, float tailWidth, Rect? placed = null)
		{
			Rect row = placed ?? GUILayoutUtility.GetRect(0, RowHeight, GUILayout.Height(RowHeight), GUILayout.ExpandWidth(true));
			bool importer = currentTab == SmartPackageTab.Importer;

			// Whole-row highlight (importer only) so the user can trace a row across
			// its left checkbox and right-side conflict badge.
			if (importer && node == importerHighlightNode && Event.current.type == EventType.Repaint)
				EditorGUI.DrawRect(row, new Color(0.24f, 0.48f, 0.90f, 0.20f));

			float midY = row.y + (RowHeight - 16f) * 0.5f;
			float indent = depth * TreeIndentWidth;
			// Deep importer trees keep the checkbox and the badge inside narrow windows.
			if (importer)
				indent = Mathf.Min(indent, Mathf.Max(0f, row.width - tailWidth - TreeFoldoutWidth - TreeToggleWidth - TreeIconWidth - 36f));
			float x = row.x + indent;

			Rect foldoutR = new Rect(x, midY, TreeFoldoutWidth, 16f);
			if (node.IsFolder && node.Children.Count > 0)
			{
				bool nx = EditorGUI.Foldout(foldoutR, node.IsExpanded, GUIContent.none, true);
				if (nx != node.IsExpanded)
				{
					node.IsExpanded = nx;
					// Keys then act on the row whose arrow was clicked, never an earlier highlighted row.
					if (importer)
						HighlightRow(node);
				}
			}
			x += TreeFoldoutWidth;

			Rect toggleR = new Rect(x, midY, TreeToggleWidth, 16f);
			PackageAssetNode.ToggleState state = importer ? GetTreeState(node) : node.GetState();
			bool prevMixed = EditorGUI.showMixedValue;
			EditorGUI.showMixedValue = state == PackageAssetNode.ToggleState.Mixed;
			bool prevChecked = state == PackageAssetNode.ToggleState.Checked;
			bool nv;
			try
			{
				if (importer)
					GUI.SetNextControlName(TreeControlName(node));
				nv = EditorGUI.Toggle(toggleR, prevChecked);
			}
			finally
			{
				EditorGUI.showMixedValue = prevMixed;
			}
			if (nv != prevChecked)
			{
				if (importer)
				{
					importerHighlightNode = node;
					treeKeyboardFocus = true;
				}
				node.SetChecked(nv, true);
				OnRowCheckChanged();
			}
			x += TreeToggleWidth + 2f;

			if (Event.current.type == EventType.Repaint)
			{
				Texture icon = GetCachedIconFor(node);
				Rect iconR = new Rect(x, midY, TreeIconWidth, 16f);
				if (icon != null)
					GUI.DrawTexture(iconR, icon, ScaleMode.ScaleToFit);
			}
			x += TreeIconWidth + 2f;

			float nameRight;
			if (importer)
			{
				nameRight = Mathf.Max(x, row.xMax - tailWidth - 2f);
				// The full path tells apart files with the same name in different folders.
				GUI.Label(new Rect(x, row.y, nameRight - x, RowHeight), new GUIContent(node.Name, node.FullPath), ImporterRowNameStyle);
			}
			else
			{
				nameRight = row.xMax - tailWidth;
				if (nameRight < x + 20f) nameRight = x + 20f;
				GUI.Label(new Rect(x, row.y, nameRight - x, RowHeight), node.Name, RowNameStyle);
			}
			Rect nameR = new Rect(x, row.y, nameRight - x, RowHeight);

			// Click the name area to highlight the whole row (importer only).
			if (importer && Event.current.type == EventType.MouseDown && Event.current.button == 0
				&& nameR.Contains(Event.current.mousePosition))
			{
				HighlightRow(node);
				Event.current.Use();
			}

			return new Rect(importer ? row.xMax - tailWidth : nameRight, row.y, tailWidth, RowHeight);
		}

		// Called when a row checkbox changes. Keeps the exporter preview behaviour
		// but only invalidates importer tallies via a version bump while in the
		// importer tab (avoids needless exporter preview rebuilds during import).
		private void OnRowCheckChanged()
		{
			if (currentTab == SmartPackageTab.Importer)
				RefreshImporterConflictPreview(false);
			else
				MarkSelectionDirty();
		}


		private static Texture GetCachedIconFor(PackageAssetNode node)
		{
			string path = node.FullPath;
			if (string.IsNullOrEmpty(path))
				return ResolveFallbackIcon(node);
			if (s_IconCache.TryGetValue(path, out Texture cached))
				return cached;

			// Exporter rows point at assets that already exist in the project, so this
			// returns the real per-type icon. Importer rows reference assets that aren't
			// in the project yet, so it returns null and we fall back to an icon derived
			// from the node's kind / file extension below.
			Texture icon = AssetDatabase.GetCachedIcon(path);
			if (icon == null)
				icon = ResolveFallbackIcon(node);
			s_IconCache[path] = icon;
			return icon;
		}

		// Used when no project asset backs the row (mainly the importer): folders get
		// the folder icon and files get an extension-based icon (.png / .anim / .prefab
		// / .mat / .cs … each show their proper project-window icon) instead of a single
		// generic icon for every file.
		private static Texture ResolveFallbackIcon(PackageAssetNode node)
		{
			if (node.IsFolder)
				return FolderIcon;

			Texture icon = null;
			if (!string.IsNullOrEmpty(node.Name))
				icon = UnityEditorInternal.InternalEditorUtility.GetIconForFile(node.Name);
			if (icon == null)
				icon = AssetPreview.GetMiniTypeThumbnail(typeof(UnityEngine.Object));
			return icon;
		}

		private static Texture s_FolderIcon;

		// The real yellow folder icon. Package-internal folders don't exist in the
		// project, so AssetDatabase.GetCachedIcon returns null for them; the built-in
		// "Folder Icon" content is the reliable source, with the always-present "Assets"
		// folder's icon as a final fallback.
		private static Texture FolderIcon
		{
			get
			{
				if (s_FolderIcon == null)
				{
					GUIContent c = EditorGUIUtility.IconContent("Folder Icon");
					s_FolderIcon = c != null ? c.image : null;
					if (s_FolderIcon == null)
						s_FolderIcon = AssetDatabase.GetCachedIcon("Assets");
				}
				return s_FolderIcon;
			}
		}

		private void DrawNode(PackageAssetNode node, int depth)
		{
			if (node == null) return;
			if (!IsNodeVisible(node)) return;

			Rect tail = DrawRowCore(node, depth, TreeSizeWidth);
			GUI.Label(tail, FormatSize(node.FileSize), RowSizeStyle);

			if (node.IsFolder && node.IsExpanded)
			{
				List<PackageAssetNode> kids = GetSortedChildren(node);
				if (kids != null)
				{
					foreach (PackageAssetNode c in kids)
						DrawNode(c, depth + 1);
				}
			}
		}

		private bool IsNodeVisible(PackageAssetNode node)
		{
			if (node == null) return false;
			EnsureVisibilityCache();
			return visibleCache.Contains(node);
		}

		private void EnsureVisibilityCache()
		{
			if (visibleCacheValid) return;
			visibleCacheValid = true;
			visibleCache.Clear();
			if (rootNode == null) return;
			ComputeVisibilityRecursive(rootNode);
		}

		private bool ComputeVisibilityRecursive(PackageAssetNode node)
		{
			if (node.IsFolder)
			{
				bool anyVisible = false;
				List<PackageAssetNode> kids = GetSortedChildren(node);
				if (kids != null)
				{
					foreach (PackageAssetNode c in kids)
					{
						if (ComputeVisibilityRecursive(c))
							anyVisible = true;
					}
				}
				bool visible = anyVisible || node.Parent == null;
				if (visible) visibleCache.Add(node);
				return visible;
			}

			string ext = node.Extension ?? string.Empty;
			if (excludeExtSet != null && !string.IsNullOrEmpty(ext) && excludeExtSet.Contains(ext.ToLowerInvariant()))
				return false;
			if (excludeRegex != null && excludeRegex.IsExcluded(node.FullPath))
				return false;
			string search = settings?.SearchText;
			if (!string.IsNullOrEmpty(search) && node.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
				return false;

			visibleCache.Add(node);
			return true;
		}

		private static string FormatSize(long bytes)
		{
			if (bytes <= 0) return "";
			if (bytes < 1024) return bytes + " B";
			if (bytes < 1024L * 1024L) return (bytes / 1024f).ToString("0.0") + " KB";
			if (bytes < 1024L * 1024L * 1024L) return (bytes / (1024f * 1024f)).ToString("0.0") + " MB";
			return (bytes / (1024f * 1024f * 1024f)).ToString("0.0") + " GB";
		}

		// Importer row: a badge per checked leaf, and on a folder the kind that most needs
		// a look among its checked contents.
		private void DrawNodeRow(PackageAssetNode node, int depth, Dictionary<string, ImportConflict> conflictByPath, Rect placed)
		{
			ImportConflictKind badgeKind = default;
			string badgeTip = null;
			bool hasBadge = true;
			if (node.IsFolder)
			{
				if (node.HasCheckedBlocked) badgeKind = ImportConflictKind.Blocked;
				else if (node.HasCheckedPathConflict) badgeKind = ImportConflictKind.PathConflict;
				else if (node.HasCheckedGuidConflict) badgeKind = ImportConflictKind.GuidConflict;
				else if (node.HasCheckedUpdate) badgeKind = ImportConflictKind.Update;
				else if (node.HasCheckedDuplicate) badgeKind = ImportConflictKind.Duplicate;
				else hasBadge = false;
			}
			else if (node.IsChecked && conflictByPath != null && conflictByPath.TryGetValue(node.FullPath, out ImportConflict c)
				&& !(c.Kind == ImportConflictKind.Duplicate && c.ExistingFromProject))
			{
				badgeKind = c.Kind;
				// A blocked file says why on its badge.
				badgeTip = c.Kind == ImportConflictKind.Blocked ? c.Message : null;
			}
			else
			{
				hasBadge = false;
			}

			float badgeW = hasBadge ? GetBadgeWidth(badgeKind) : 0f;
			Rect tail = DrawRowCore(node, depth, badgeW, placed);
			if (hasBadge)
			{
				Rect badgeR = new Rect(tail.x, tail.y + (RowHeight - 16f) * 0.5f, badgeW, 16f);
				DrawConflictBadge(badgeR, badgeKind, badgeTip);
			}
		}

		private float GetBadgeWidth(ImportConflictKind kind)
		{
			// Widths follow the label language.
			if (badgeLanguage != YanKLocalization.CurrentLanguage)
			{
				badgeWidths.Clear();
				badgeLanguage = YanKLocalization.CurrentLanguage;
			}
			if (!badgeWidths.TryGetValue(kind, out float width))
				badgeWidths[kind] = width = EditorStyles.miniLabel.CalcSize(new GUIContent(ImportConflictLabels.Kind(kind))).x + 12f;
			return width;
		}

		private static Color BadgeColor(ImportConflictKind kind)
		{
			switch (kind)
			{
				case ImportConflictKind.New: return NewColor;
				case ImportConflictKind.Duplicate: return IdenticalColor;
				case ImportConflictKind.Update: return UpdateColor;
				default: return ConflictColor;
			}
		}

		private static void DrawConflictBadge(Rect r, ImportConflictKind kind, string tooltip)
		{
			if (Event.current.type != EventType.Repaint)
				return;
			EditorGUI.DrawRect(r, BadgeColor(kind));
			GUI.Label(r, new GUIContent(ImportConflictLabels.Kind(kind), tooltip), EditorStyles.whiteMiniLabel);
		}
	}
}
