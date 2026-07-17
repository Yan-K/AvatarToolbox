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
		private sealed class PackageLoadError
		{
			public string Path;
			public string Error;
			public int Generation;
		}

		private readonly ConcurrentQueue<PackageLoadError> loadErrorQueue = new ConcurrentQueue<PackageLoadError>();
		private readonly HashSet<string> inFlightPaths = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
		private SemaphoreSlim loadSemaphore;
		private int pendingLoads;
		private bool pollHooked;
		private long nextPackageDropOrder;
		private int loadGeneration;
		private SnapshotAssetProbe importerProjectSnapshot;
		private HashSet<string> importerContentUnknownPaths = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
		private bool dirtyTargetRefreshQueued;
		private bool importQueued;

		// Bumped whenever a checkbox changes in the importer; package tallies recompute
		// lazily when their stored version no longer matches.
		private int importerCheckVersion;

		// Currently highlighted row (importer only) so the user can trace a row from
		// its left-side checkbox to its right-side conflict badge in a long list.
		private PackageAssetNode importerHighlightNode;

		private void EnsureImporterInit()
		{
			// Same domain-reload guard as the exporter: the bool flag can survive a reload
			// as true while loadSemaphore (not serialized) comes back null, so re-init when
			// the semaphore is missing to avoid null dereferences in the load pipeline.
			if (importerInitialized && loadSemaphore != null) return;
			importerInitialized = true;
			int storedPolicy = EditorPrefs.GetInt(Prefs.ConflictPolicy, (int)ConflictPolicy.Ask);
			importerPolicy = System.Enum.IsDefined(typeof(ConflictPolicy), storedPolicy)
				? (ConflictPolicy)storedPolicy
				: ConflictPolicy.Ask;
			loadSemaphore = new SemaphoreSlim(System.Math.Max(1, System.Math.Min(4, System.Environment.ProcessorCount)));
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
			{
				loadedPackages.RemoveAt(removeIndex);
				RefreshImporterConflictPreview(false);
			}
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
			{
				loadGeneration++;
				loadedPackages.Clear();
				inFlightPaths.Clear();
				Interlocked.Exchange(ref pendingLoads, 0);
				while (loadedQueue.TryDequeue(out _)) { }
				while (loadErrorQueue.TryDequeue(out _)) { }
				importerProjectSnapshot = null;
				importerContentUnknownPaths.Clear();
				importerHighlightNode = null;
			}

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
				RefreshImporterConflictPreview(false);
			}

			using (new EditorGUI.DisabledScope(loadedPackages.Count == 0 || pendingLoads > 0 || importQueued))
			{
				if (DrawColoredToolbarButton(L("yspImport", "Import…"), 90))
					QueueImport();
			}

			EditorGUILayout.EndHorizontal();
		}

		// Dedupes against already-loaded and in-flight packages, then kicks off a
		// background metadata scan for each new path on a bounded thread pool.
		private void QueuePackages(IEnumerable<string> paths)
		{
			if (paths == null) return;
			EnsureImporterInit();

			List<KeyValuePair<string, long>> toLoad = new List<KeyValuePair<string, long>>();
			foreach (string path in paths)
			{
				if (string.IsNullOrEmpty(path))
					continue;
				string canonicalPath;
				try { canonicalPath = Path.GetFullPath(path); }
				catch (System.Exception) { continue; }
				bool already = false;
				foreach (LoadedPackage existing in loadedPackages)
				{
					if (string.Equals(existing.FilePath, canonicalPath, System.StringComparison.OrdinalIgnoreCase)) { already = true; break; }
				}
				if (already || inFlightPaths.Contains(canonicalPath))
					continue;
				inFlightPaths.Add(canonicalPath);
				toLoad.Add(new KeyValuePair<string, long>(canonicalPath, nextPackageDropOrder++));
			}

			if (toLoad.Count == 0)
				return;

			foreach (KeyValuePair<string, long> pending in toLoad)
			{
				Interlocked.Increment(ref pendingLoads);
				string capturedPath = pending.Key;
				long capturedOrder = pending.Value;
				int capturedGeneration = loadGeneration;
				Task.Run(() => LoadPackageWorker(capturedPath, capturedOrder, capturedGeneration));
			}

			HookPoll();
		}

		// Runs off the main thread and performs archive/file IO only; no Unity API calls.
		private void LoadPackageWorker(string path, long dropOrder, int generation)
		{
			loadSemaphore.Wait();
			try
			{
				List<UnityPackageEntry> entries = UnityPackageReader.ReadMetadata(path);

				PackageAssetNode tree = PackageAssetNode.BuildProjectTree(entries);
				// Sort once off the main thread so per-frame drawing never re-sorts.
				tree.SortChildrenRecursive();

				LoadedPackage pkg = new LoadedPackage
				{
					FilePath = path,
					DropOrder = dropOrder,
					LoadGeneration = generation,
					FileLength = new FileInfo(path).Length,
					FileLastWriteUtcTicks = File.GetLastWriteTimeUtc(path).Ticks,
					Entries = entries,
					Tree = tree,
					LeafCount = tree.CountLeaves(),
					IsExpanded = true
				};

				loadedQueue.Enqueue(pkg);
			}
			catch (System.Exception ex)
			{
				loadErrorQueue.Enqueue(new PackageLoadError
				{
					Path = path,
					Error = ex.ToString(),
					Generation = generation
				});
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
			// Background reads cannot be force-aborted safely. Invalidate their generation
			// so any late completion is ignored instead of reviving a closed/cleared window.
			loadGeneration++;
			Interlocked.Exchange(ref pendingLoads, 0);
			inFlightPaths.Clear();
			while (loadedQueue.TryDequeue(out _)) { }
			while (loadErrorQueue.TryDequeue(out _)) { }
			if (pollHooked)
			{
				EditorApplication.update -= PollLoadQueue;
				pollHooked = false;
			}
			EditorApplication.delayCall -= RefreshDirtyTargetsAfterFocus;
			EditorApplication.delayCall -= RunQueuedImport;
			dirtyTargetRefreshQueued = false;
			importQueued = false;
			ImportSession.ResetConflictWindowPosition();
		}

		private void OnFocus()
		{
			if (!importerInitialized || currentTab != SmartPackageTab.Importer || loadedPackages.Count == 0)
				return;
			QueueDirtyTargetRefresh();
		}

		private void QueueDirtyTargetRefresh()
		{
			if (dirtyTargetRefreshQueued)
				return;
			dirtyTargetRefreshQueued = true;
			EditorApplication.delayCall += RefreshDirtyTargetsAfterFocus;
		}

		private void RefreshDirtyTargetsAfterFocus()
		{
			dirtyTargetRefreshQueued = false;
			if (this == null || !importerInitialized || loadedPackages.Count == 0)
				return;

			bool saved = SnapshotAssetProbe.SaveDirtyTargets(
				loadedPackages, out HashSet<string> unknownPaths);
			bool unknownChanged = !importerContentUnknownPaths.SetEquals(unknownPaths);
			if (!saved && !unknownChanged)
				return;

			importerContentUnknownPaths = unknownPaths;
			RefreshImporterConflictPreview(true);
			Repaint();
		}

		private void QueueImport()
		{
			if (importQueued)
				return;
			importQueued = true;
			EditorApplication.delayCall += RunQueuedImport;
		}

		private void RunQueuedImport()
		{
			if (this == null)
				return;
			try
			{
				if (loadedPackages.Count == 0 || pendingLoads > 0)
					return;
				ImportRunResult importResult = ImportSession.Apply(loadedPackages, importerPolicy);
				if (importResult.ShouldClear)
				{
					loadedPackages.Clear();
					importerScroll = Vector2.zero;
					importerHighlightNode = null;
					importerProjectSnapshot = null;
					importerContentUnknownPaths.Clear();
				}
			}
			finally
			{
				importQueued = false;
				Repaint();
			}
		}

		private void PollLoadQueue()
		{
			bool changed = false;

			while (loadedQueue.TryDequeue(out LoadedPackage pkg))
			{
				if (pkg.LoadGeneration != loadGeneration)
					continue;
				inFlightPaths.Remove(pkg.FilePath);
				bool already = false;
				foreach (LoadedPackage existing in loadedPackages)
				{
					if (existing.FilePath == pkg.FilePath) { already = true; break; }
				}
				if (!already)
					loadedPackages.Add(pkg);
				loadedPackages.Sort((a, b) => a.DropOrder.CompareTo(b.DropOrder));
				Interlocked.Decrement(ref pendingLoads);
				changed = true;
			}

			while (loadErrorQueue.TryDequeue(out PackageLoadError err))
			{
				if (err.Generation != loadGeneration)
					continue;
				inFlightPaths.Remove(err.Path);
				Interlocked.Decrement(ref pendingLoads);
				Debug.LogError("[YSP] Failed to read package:\n" + err.Path + "\n" + err.Error);
				changed = true;
			}

			if (changed)
			{
				SnapshotAssetProbe.SaveDirtyTargets(
					loadedPackages, out importerContentUnknownPaths);
				RefreshImporterConflictPreview(true);
				Repaint();
			}

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
			int total = pkg.LeafCount;
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

		private void RefreshImporterConflictPreview(bool refreshProjectSnapshot)
		{
			if (loadedPackages.Count == 0)
				return;
			if (refreshProjectSnapshot || importerProjectSnapshot == null)
				// Content fingerprints are loaded lazily and cached only for paths the
				// packages actually touch. This lets the UI distinguish Identical from a
				// real GUID Conflict without hashing the entire project.
				importerProjectSnapshot = SnapshotAssetProbe.Capture(
					includeContent: true,
					contentUnknownPaths: importerContentUnknownPaths);

			for (int i = 0; i < loadedPackages.Count; i++)
			{
				LoadedPackage package = loadedPackages[i];
				package.ConflictByGuid.Clear();
				package.ConflictByPath.Clear();
				package.TallyVersion = -1;
			}

			ImportPlan preview = ImportPlanBuilder.Build(loadedPackages, importerProjectSnapshot, importerPolicy);
			for (int i = 0; i < preview.OrderedItems.Count; i++)
			{
				ImportPlanItem item = preview.OrderedItems[i];
				if (item.Package == null || item.Entry == null)
					continue;
				item.Package.ConflictByGuid[item.Entry.Guid] = item.Conflict;
				item.Package.ConflictByPath[item.IncomingPath] = item.Conflict;
			}

			importerCheckVersion++;
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

			int guid = 0, path = 0, update = 0, duplicate = 0, selected = 0;
			AggregateConflicts(pkg.Tree, pkg.ConflictByPath, ref guid, ref path, ref update, ref duplicate, ref selected);
			pkg.GuidConflictCount = guid;
			pkg.PathConflictCount = path;
			pkg.UpdateCount = update;
			pkg.DuplicateCount = duplicate;
			pkg.SelectedCount = selected;
		}

		private static void AggregateConflicts(PackageAssetNode node, Dictionary<string, ImportConflict> map,
			ref int guid, ref int path, ref int update, ref int duplicate, ref int selected)
		{
			if (!node.IsFolder || (node.IsFolder && node.Children.Count == 0 && node.HasPackageEntry))
			{
				node.HasCheckedGuidConflict = false;
				node.HasCheckedPathConflict = false;
				node.HasCheckedUpdate = false;
				node.HasCheckedDuplicate = false;
				if (node.IsChecked)
				{
					selected++;
					if (map != null && map.TryGetValue(node.FullPath, out ImportConflict c))
					{
						if (c.Kind == ImportConflictKind.GuidConflict) { guid++; node.HasCheckedGuidConflict = true; }
						else if (c.Kind == ImportConflictKind.PathConflict) { path++; node.HasCheckedPathConflict = true; }
						else if (c.Kind == ImportConflictKind.Update) { update++; node.HasCheckedUpdate = true; }
						else if (c.Kind == ImportConflictKind.Duplicate && !c.ExistingFromProject)
						{
							duplicate++;
							node.HasCheckedDuplicate = true;
						}
					}
				}
				return;
			}

			bool folderGuid = false, folderPath = false, folderUpdate = false, folderDuplicate = false;
			List<PackageAssetNode> children = node.Children;
			for (int i = 0; i < children.Count; i++)
			{
				PackageAssetNode child = children[i];
				AggregateConflicts(child, map, ref guid, ref path, ref update, ref duplicate, ref selected);
				folderGuid |= child.HasCheckedGuidConflict;
				folderPath |= child.HasCheckedPathConflict;
				folderUpdate |= child.HasCheckedUpdate;
				folderDuplicate |= child.HasCheckedDuplicate;
			}
			// A non-empty folder can also carry its own package .meta record. It is
			// implicitly selected whenever at least one descendant is selected, so expose
			// its conflict on the folder badge without inflating the user-facing file count.
			if (node.HasPackageEntry && node.GetState() != PackageAssetNode.ToggleState.Unchecked
				&& map != null && map.TryGetValue(node.FullPath, out ImportConflict folderConflict))
			{
				folderGuid |= folderConflict.Kind == ImportConflictKind.GuidConflict;
				folderPath |= folderConflict.Kind == ImportConflictKind.PathConflict;
				folderUpdate |= folderConflict.Kind == ImportConflictKind.Update;
				folderDuplicate |= folderConflict.Kind == ImportConflictKind.Duplicate
					&& !folderConflict.ExistingFromProject;
			}
			node.HasCheckedGuidConflict = folderGuid;
			node.HasCheckedPathConflict = folderPath;
			node.HasCheckedUpdate = folderUpdate;
			node.HasCheckedDuplicate = folderDuplicate;
		}

		// Always-visible project occupancy counts on the card header (independent of
		// foldout). Internal planning keeps its detailed conflict kinds, while the UI
		// deliberately presents only Identical package duplicates or GUID Conflict.
		private void DrawPackageConflictTallies(LoadedPackage pkg)
		{
			if (pkg == null) return;

			int conflictCount = pkg.GuidConflictCount + pkg.PathConflictCount + pkg.UpdateCount;
			if (conflictCount > 0)
			{
				string label = string.Format(L("yspGuidConflictCount", "{0} GUID Conflict"), conflictCount);
				DrawTallyBadge(label, new Color(0.85f, 0.20f, 0.20f, 0.90f));
			}
			if (pkg.DuplicateCount > 0)
			{
				string label = string.Format(L("yspIdenticalCount", "{0} Identical"), pkg.DuplicateCount);
				DrawTallyBadge(label, new Color(0.25f, 0.50f, 0.90f, 0.85f));
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
