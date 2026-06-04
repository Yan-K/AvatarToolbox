using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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

		// --- Background loading state ---
		private readonly ConcurrentQueue<LoadedPackage> loadedQueue = new ConcurrentQueue<LoadedPackage>();
		private readonly ConcurrentQueue<string> loadErrorQueue = new ConcurrentQueue<string>();
		private readonly HashSet<string> inFlightPaths = new HashSet<string>();
		private SemaphoreSlim loadSemaphore;
		private int pendingLoads;
		private bool pollHooked;

		// Bumped whenever a checkbox changes in the importer; package tallies recompute
		// lazily when their stored version no longer matches.
		private int importerCheckVersion;

		// Currently highlighted row (importer only) so the user can trace a row from
		// its left-side checkbox to its right-side conflict badge in a long list.
		private PackageAssetNode importerHighlightNode;

		private void EnsureImporterInit()
		{
			if (importerInitialized) return;
			importerInitialized = true;
			importerPolicy = (ConflictPolicy)EditorPrefs.GetInt(Prefs.ConflictPolicy, (int)ConflictPolicy.Ask);
			loadSemaphore = new SemaphoreSlim(System.Math.Max(1, System.Environment.ProcessorCount));
		}

		private void DrawImporterTab()
		{
			EnsureImporterInit();

			DrawImporterToolbar();

			GUILayout.Space(4);

			// The entire list area below the toolbar is a drop target at all times,
			// whether the list is empty or already showing packages.
			Rect listArea = EditorGUILayout.BeginVertical(GUILayout.ExpandHeight(true));

			importerScroll = EditorGUILayout.BeginScrollView(importerScroll);
			int removeIndex = -1;
			if (loadedPackages.Count == 0)
			{
				DrawImporterEmptyHint();
			}
			else
			{
				for (int i = 0; i < loadedPackages.Count; i++)
				{
					if (DrawPackageCard(loadedPackages[i]))
						removeIndex = i;
				}
			}
			EditorGUILayout.EndScrollView();

			EditorGUILayout.EndVertical();

			HandleListAreaDrop(listArea);

			if (removeIndex >= 0)
				loadedPackages.RemoveAt(removeIndex);
		}

		private void DrawImporterEmptyHint()
		{
			int pending = pendingLoads;
			string label = pending > 0
				? string.Format(L("yspLoadingPackages", "Loading {0} package(s)…"), pending)
				: L("yspImporterDropZone", "Drag .unitypackage file(s) here");

			GUILayout.FlexibleSpace();
			GUIStyle centered = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
			{
				fontSize = 12,
				wordWrap = true
			};
			GUILayout.Label(label, centered);
			GUILayout.FlexibleSpace();
		}

		// Accepts .unitypackage drops anywhere over the list area.
		private void HandleListAreaDrop(Rect area)
		{
			Event evt = Event.current;
			if (!area.Contains(evt.mousePosition)) return;

			switch (evt.type)
			{
				case EventType.DragUpdated:
					DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
					evt.Use();
					break;
				case EventType.DragPerform:
					DragAndDrop.AcceptDrag();
					List<string> dropped = new List<string>();
					foreach (string p in DragAndDrop.paths)
					{
						if (!string.IsNullOrEmpty(p) && p.EndsWith(".unitypackage", System.StringComparison.OrdinalIgnoreCase))
							dropped.Add(p);
					}
					QueuePackages(dropped);
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
					QueuePackages(new[] { picked });
				}
			}

			if (GUILayout.Button(L("yspClearPackages", "Clear"), EditorStyles.toolbarButton, GUILayout.Width(60)))
				loadedPackages.Clear();

			using (new EditorGUI.DisabledScope(loadedPackages.Count == 0))
			{
				if (GUILayout.Button(L("yspExpandAll", "Expand All"), EditorStyles.toolbarButton, GUILayout.Width(80)))
					SetAllPackagesExpanded(true);
				if (GUILayout.Button(L("yspFoldAll", "Fold All"), EditorStyles.toolbarButton, GUILayout.Width(70)))
					SetAllPackagesExpanded(false);
			}

			GUILayout.FlexibleSpace();

			GUILayout.Label(L("yspConflictPolicy", "Conflict Action"), EditorStyles.miniLabel, GUILayout.Width(100));
			ConflictPolicy newPolicy = (ConflictPolicy)EditorGUILayout.EnumPopup(importerPolicy, EditorStyles.toolbarPopup, GUILayout.Width(110));
			if (newPolicy != importerPolicy)
			{
				importerPolicy = newPolicy;
				EditorPrefs.SetInt(Prefs.ConflictPolicy, (int)importerPolicy);
			}

			using (new EditorGUI.DisabledScope(loadedPackages.Count == 0 || pendingLoads > 0))
			{
				if (DrawColoredToolbarButton(L("yspImport", "Import…"), 90))
				{
					bool completed = ImportSession.Apply(loadedPackages, importerPolicy);
					if (completed)
					{
						loadedPackages.Clear();
						importerScroll = Vector2.zero;
					}
				}
			}

			EditorGUILayout.EndHorizontal();
		}

		// Dedupes against already-loaded and in-flight packages, then kicks off a
		// background metadata scan for each new path on a bounded thread pool.
		private void QueuePackages(IEnumerable<string> paths)
		{
			if (paths == null) return;
			EnsureImporterInit();

			List<string> toLoad = new List<string>();
			foreach (string path in paths)
			{
				if (string.IsNullOrEmpty(path))
					continue;
				bool already = false;
				foreach (LoadedPackage existing in loadedPackages)
				{
					if (existing.FilePath == path) { already = true; break; }
				}
				if (already || inFlightPaths.Contains(path))
					continue;
				inFlightPaths.Add(path);
				toLoad.Add(path);
			}

			if (toLoad.Count == 0)
				return;

			// Snapshot the project's GUID<->path map once on the main thread so the
			// background conflict resolution stays free of Unity API calls.
			SnapshotAssetProbe probe = SnapshotAssetProbe.Capture();

			foreach (string path in toLoad)
			{
				Interlocked.Increment(ref pendingLoads);
				string capturedPath = path;
				Task.Run(() => LoadPackageWorker(capturedPath, probe));
			}

			HookPoll();
		}

		// Runs off the main thread. No Unity API beyond the pre-captured probe.
		private void LoadPackageWorker(string path, IAssetProbe probe)
		{
			loadSemaphore.Wait();
			try
			{
				List<UnityPackageEntry> entries = UnityPackageReader.ReadMetadata(path);

				Dictionary<string, ImportConflict> conflictByGuid = ImportConflictResolver.Resolve(entries, probe);

				List<string> assetPaths = new List<string>(entries.Count);
				Dictionary<string, ImportConflict> conflictByPath = new Dictionary<string, ImportConflict>(entries.Count);
				foreach (UnityPackageEntry e in entries)
				{
					if (e == null || string.IsNullOrEmpty(e.AssetPath))
						continue;
					assetPaths.Add(e.AssetPath);
					if (conflictByGuid.TryGetValue(e.Guid, out ImportConflict c))
						conflictByPath[e.AssetPath] = c;
				}

				PackageAssetNode tree = PackageAssetNode.BuildTree(assetPaths);
				// Sort once off the main thread so per-frame drawing never re-sorts.
				tree.SortChildrenRecursive();

				LoadedPackage pkg = new LoadedPackage
				{
					FilePath = path,
					Entries = entries,
					Tree = tree,
					ConflictByGuid = conflictByGuid,
					ConflictByPath = conflictByPath,
					IsExpanded = true
				};

				loadedQueue.Enqueue(pkg);
			}
			catch (System.Exception ex)
			{
				loadErrorQueue.Enqueue(path + "\n" + ex.Message);
			}
			finally
			{
				loadSemaphore.Release();
			}
		}

		private void HookPoll()
		{
			if (pollHooked) return;
			pollHooked = true;
			EditorApplication.update += PollLoadQueue;
		}

		private void OnDisable()
		{
			if (pollHooked)
			{
				EditorApplication.update -= PollLoadQueue;
				pollHooked = false;
			}
		}

		private void PollLoadQueue()
		{
			bool changed = false;

			while (loadedQueue.TryDequeue(out LoadedPackage pkg))
			{
				inFlightPaths.Remove(pkg.FilePath);
				bool already = false;
				foreach (LoadedPackage existing in loadedPackages)
				{
					if (existing.FilePath == pkg.FilePath) { already = true; break; }
				}
				if (!already)
					loadedPackages.Add(pkg);
				Interlocked.Decrement(ref pendingLoads);
				changed = true;
			}

			while (loadErrorQueue.TryDequeue(out string err))
			{
				int nl = err.IndexOf('\n');
				string failedPath = nl > 0 ? err.Substring(0, nl) : err;
				inFlightPaths.Remove(failedPath);
				Interlocked.Decrement(ref pendingLoads);
				Debug.LogError("[YSP] Failed to read package:\n" + err);
				changed = true;
			}

			if (changed)
				Repaint();

			if (pendingLoads <= 0 && loadedQueue.IsEmpty && loadErrorQueue.IsEmpty)
			{
				EditorApplication.update -= PollLoadQueue;
				pollHooked = false;
			}
		}

		private bool DrawPackageCard(LoadedPackage pkg)
		{
			bool remove = false;

			EnsurePackageTally(pkg);

			EditorGUILayout.BeginVertical(EditorStyles.helpBox);

			EditorGUILayout.BeginHorizontal();
			int total = pkg.Entries.Count;
			int selected = pkg.SelectedCount;
			string fileName = Path.GetFileName(pkg.FilePath);
			pkg.IsExpanded = EditorGUILayout.Foldout(pkg.IsExpanded,
				string.Format("{0}   ({1} / {2})", fileName, selected, total), true);

			GUILayout.FlexibleSpace();
			DrawPackageConflictTallies(pkg);

			if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22)))
				remove = true;
			EditorGUILayout.EndHorizontal();

			if (pkg.IsExpanded && pkg.Tree != null)
				DrawNode(pkg.Tree, 0, pkg.ConflictByPath);

			EditorGUILayout.EndVertical();
			return remove;
		}

		private void SetAllPackagesExpanded(bool expanded)
		{
			for (int i = 0; i < loadedPackages.Count; i++)
				loadedPackages[i].IsExpanded = expanded;
		}

		// Recomputes a package's conflict / selected tallies (counting only CHECKED
		// leaves) and the per-folder conflict flags, but only when the checked state
		// has actually changed since the last computation.
		private void EnsurePackageTally(LoadedPackage pkg)
		{
			if (pkg == null || pkg.Tree == null)
				return;
			if (pkg.TallyVersion == importerCheckVersion)
				return;
			pkg.TallyVersion = importerCheckVersion;

			int guid = 0, path = 0, update = 0, selected = 0;
			AggregateConflicts(pkg.Tree, pkg.ConflictByPath, ref guid, ref path, ref update, ref selected);
			pkg.GuidConflictCount = guid;
			pkg.PathConflictCount = path;
			pkg.UpdateCount = update;
			pkg.SelectedCount = selected;
		}

		private static void AggregateConflicts(PackageAssetNode node, Dictionary<string, ImportConflict> map,
			ref int guid, ref int path, ref int update, ref int selected)
		{
			if (!node.IsFolder)
			{
				node.HasCheckedGuidConflict = false;
				node.HasCheckedPathConflict = false;
				node.HasCheckedUpdate = false;
				if (node.IsChecked)
				{
					selected++;
					if (map != null && map.TryGetValue(node.FullPath, out ImportConflict c))
					{
						if (c.Kind == ImportConflictKind.GuidConflict) { guid++; node.HasCheckedGuidConflict = true; }
						else if (c.Kind == ImportConflictKind.PathConflict) { path++; node.HasCheckedPathConflict = true; }
						else if (c.Kind == ImportConflictKind.Update) { update++; node.HasCheckedUpdate = true; }
					}
				}
				return;
			}

			bool folderGuid = false, folderPath = false, folderUpdate = false;
			List<PackageAssetNode> children = node.Children;
			for (int i = 0; i < children.Count; i++)
			{
				PackageAssetNode child = children[i];
				AggregateConflicts(child, map, ref guid, ref path, ref update, ref selected);
				folderGuid |= child.HasCheckedGuidConflict;
				folderPath |= child.HasCheckedPathConflict;
				folderUpdate |= child.HasCheckedUpdate;
			}
			node.HasCheckedGuidConflict = folderGuid;
			node.HasCheckedPathConflict = folderPath;
			node.HasCheckedUpdate = folderUpdate;
		}

		// Always-visible conflict counts on the card header (independent of foldout).
		private void DrawPackageConflictTallies(LoadedPackage pkg)
		{
			if (pkg == null) return;

			if (pkg.GuidConflictCount > 0)
			{
				string label = string.Format(L("yspGuidConflictCount", "{0} GUID conflict"), pkg.GuidConflictCount);
				DrawTallyBadge(label, new Color(0.90f, 0.30f, 0.30f, 0.85f));
			}
			if (pkg.PathConflictCount > 0)
			{
				string label = string.Format(L("yspPathConflictCount", "{0} path conflict"), pkg.PathConflictCount);
				DrawTallyBadge(label, new Color(0.90f, 0.75f, 0.20f, 0.85f));
			}
			if (pkg.UpdateCount > 0)
			{
				string label = string.Format(L("yspUpdateCount", "{0} overwrite"), pkg.UpdateCount);
				DrawTallyBadge(label, new Color(0.30f, 0.55f, 0.90f, 0.85f));
			}
		}

		private static void DrawTallyBadge(string label, Color bg)
		{
			GUIContent content = new GUIContent(label);
			Vector2 size = EditorStyles.miniLabel.CalcSize(content);
			Rect r = GUILayoutUtility.GetRect(size.x + 12f, 16f, GUILayout.Width(size.x + 12f), GUILayout.Height(16f));
			EditorGUI.DrawRect(r, bg);
			GUI.Label(r, label, EditorStyles.whiteMiniLabel);
		}
	}
}
