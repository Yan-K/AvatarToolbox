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

		private static GUIStyle s_RowName;
		private static GUIStyle s_RowSize;

		private static GUIStyle RowNameStyle
		{
			get
			{
				if (s_RowName == null)
					s_RowName = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleLeft };
				return s_RowName;
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

		private Rect DrawRowCore(PackageAssetNode node, int depth, float tailWidth)
		{
			Rect row = GUILayoutUtility.GetRect(0, RowHeight, GUILayout.Height(RowHeight), GUILayout.ExpandWidth(true));
			bool importer = currentTab == SmartPackageTab.Importer;

			// Whole-row highlight (importer only) so the user can trace a row across
			// its left checkbox and right-side conflict badge.
			if (importer && node == importerHighlightNode && Event.current.type == EventType.Repaint)
				EditorGUI.DrawRect(row, new Color(0.24f, 0.48f, 0.90f, 0.20f));

			float midY = row.y + (RowHeight - 16f) * 0.5f;
			float x = row.x + depth * TreeIndentWidth;

			Rect foldoutR = new Rect(x, midY, TreeFoldoutWidth, 16f);
			if (node.IsFolder && node.Children.Count > 0)
			{
				bool nx = EditorGUI.Foldout(foldoutR, node.IsExpanded, GUIContent.none, true);
				if (nx != node.IsExpanded)
					node.IsExpanded = nx;
			}
			x += TreeFoldoutWidth;

			Rect toggleR = new Rect(x, midY, TreeToggleWidth, 16f);
			PackageAssetNode.ToggleState state = node.GetState();
			bool prevMixed = EditorGUI.showMixedValue;
			EditorGUI.showMixedValue = state == PackageAssetNode.ToggleState.Mixed;
			bool prevChecked = state == PackageAssetNode.ToggleState.Checked;
			bool nv = EditorGUI.Toggle(toggleR, prevChecked);
			EditorGUI.showMixedValue = prevMixed;
			if (nv != prevChecked)
			{
				node.SetChecked(nv, true);
				node.Parent?.RecomputeFromChildren();
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

			float nameRight = row.xMax - tailWidth;
			if (nameRight < x + 20f) nameRight = x + 20f;
			Rect nameR = new Rect(x, row.y, nameRight - x, RowHeight);
			GUI.Label(nameR, node.Name, RowNameStyle);

			// Click the name area to highlight the whole row (importer only).
			if (importer && Event.current.type == EventType.MouseDown && Event.current.button == 0
				&& nameR.Contains(Event.current.mousePosition))
			{
				importerHighlightNode = node;
				Repaint();
				Event.current.Use();
			}

			return new Rect(nameRight, row.y, tailWidth, RowHeight);
		}

		// Called when a row checkbox changes. Keeps the exporter preview behaviour
		// but only invalidates importer tallies via a version bump while in the
		// importer tab (avoids needless exporter preview rebuilds during import).
		private void OnRowCheckChanged()
		{
			if (currentTab == SmartPackageTab.Importer)
				importerCheckVersion++;
			else
				MarkSelectionDirty();
		}


		private static Texture GetCachedIconFor(PackageAssetNode node)
		{
			string path = node.FullPath;
			if (string.IsNullOrEmpty(path))
				return AssetPreview.GetMiniTypeThumbnail(node.IsFolder ? typeof(UnityEditor.DefaultAsset) : typeof(UnityEngine.Object));
			if (s_IconCache.TryGetValue(path, out Texture cached))
				return cached;
			Texture icon = AssetDatabase.GetCachedIcon(path);
			if (icon == null)
				icon = AssetPreview.GetMiniTypeThumbnail(node.IsFolder ? typeof(UnityEditor.DefaultAsset) : typeof(UnityEngine.Object));
			s_IconCache[path] = icon;
			return icon;
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

		// Importer variant: no exporter filters; draws conflict badge per leaf (only
		// when checked) and an aggregate badge on folders containing checked conflicts.
		private void DrawNode(PackageAssetNode node, int depth, Dictionary<string, ImportConflict> conflictByPath)
		{
			if (node == null) return;

			ImportConflictKind badgeKind = default;
			bool hasBadge = false;
			if (node.IsFolder)
			{
				if (node.HasCheckedGuidConflict) { badgeKind = ImportConflictKind.GuidConflict; hasBadge = true; }
				else if (node.HasCheckedPathConflict) { badgeKind = ImportConflictKind.PathConflict; hasBadge = true; }
				else if (node.HasCheckedUpdate) { badgeKind = ImportConflictKind.Update; hasBadge = true; }
			}
			else if (node.IsChecked && conflictByPath != null && conflictByPath.TryGetValue(node.FullPath, out ImportConflict c))
			{
				badgeKind = c.Kind;
				hasBadge = true;
			}

			float badgeW = hasBadge ? GetBadgeWidth(badgeKind) : 0f;

			Rect tail = DrawRowCore(node, depth, badgeW);
			if (hasBadge)
			{
				Rect badgeR = new Rect(tail.x, tail.y + (RowHeight - 16f) * 0.5f, badgeW, 16f);
				DrawConflictBadge(badgeR, badgeKind);
			}

			if (node.IsFolder && node.IsExpanded)
			{
				// Children were sorted once at load time; iterate directly.
				List<PackageAssetNode> kids = node.Children;
				for (int i = 0; i < kids.Count; i++)
					DrawNode(kids[i], depth + 1, conflictByPath);
			}
		}

		private float GetBadgeWidth(ImportConflictKind kind)
		{
			string label = BadgeLabel(kind);
			GUIContent c = new GUIContent(label);
			return EditorStyles.miniLabel.CalcSize(c).x + 12f;
		}

		private string BadgeLabel(ImportConflictKind kind)
		{
			switch (kind)
			{
				case ImportConflictKind.New: return L("yspConflictNew", "New");
				case ImportConflictKind.Update: return L("yspConflictUpdate", "Existed");
				case ImportConflictKind.PathConflict: return L("yspConflictPath", "Path conflict");
				case ImportConflictKind.GuidConflict: return L("yspConflictGuid", "GUID conflict");
				default: return kind.ToString();
			}
		}

		private void DrawConflictBadge(Rect r, ImportConflictKind kind)
		{
			string label = BadgeLabel(kind);
			Color bg;
			switch (kind)
			{
				case ImportConflictKind.New:
					bg = new Color(0.30f, 0.70f, 0.30f, 0.85f);
					break;
				case ImportConflictKind.Update:
					bg = new Color(0.30f, 0.55f, 0.90f, 0.85f);
					break;
				case ImportConflictKind.PathConflict:
					bg = new Color(0.90f, 0.75f, 0.20f, 0.85f);
					break;
				case ImportConflictKind.GuidConflict:
					bg = new Color(0.90f, 0.30f, 0.30f, 0.85f);
					break;
				default:
					bg = new Color(0.5f, 0.5f, 0.5f, 0.85f);
					break;
			}

			EditorGUI.DrawRect(r, bg);
			GUI.Label(r, label, EditorStyles.whiteMiniLabel);
		}
	}
}
