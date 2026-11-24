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
		// Height of the package list's scroll view; package trees draw only the rows inside it.
		private float importerViewHeight = 400f;
		private ConflictPolicy importerPolicy;
		private bool importerInitialized;

		// --- Background loading state ---
		private readonly ConcurrentQueue<LoadedPackage> loadedQueue = new ConcurrentQueue<LoadedPackage>();
		private sealed class PackageLoadError
		{
			public string Path;
			public string Error;
			public int Generation;
			public bool Cancelled;
		}

		// One package being read on a worker. The worker writes BytesRead; the main thread reports
		// it to Unity's background tasks, whose Cancel sets Cancelled.
		private sealed class PendingLoad
		{
			public string Path;
			public string ProjectRoot;
			public string PayloadDirectory;
			public long DropOrder;
			public int Generation;
			public long FileLength;
			public long BytesRead;
			public volatile bool Cancelled;
			public int ProgressId = -1;
		}

		private readonly ConcurrentQueue<PackageLoadError> loadErrorQueue = new ConcurrentQueue<PackageLoadError>();
		private readonly Dictionary<string, PendingLoad> inFlightLoads = new Dictionary<string, PendingLoad>(System.StringComparer.OrdinalIgnoreCase);
		private SemaphoreSlim loadSemaphore;
		private bool pollHooked;
		private long nextPackageDropOrder;
		private int loadGeneration;
		private SnapshotAssetProbe importerProjectSnapshot;
		private HashSet<string> importerContentUnknownPaths = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
		private bool dirtyTargetRefreshQueued;
		private bool importQueued;

		// --- Background conflict preview ---
		// Requests coalesce for a moment, then one worker plans copies of the selection while the
		// window stays responsive. A result older than the latest request is discarded.
		private bool previewRequested;
		private bool previewSnapshotDirty;
		private bool previewHooked;
		private int previewGeneration;
		private int runningPreviewGeneration;
		private double previewDueAt;
		private Task<ImportPlan> importerPreviewTask;
		private Dictionary<LoadedPackage, LoadedPackage> previewOwners;

		// Bumped whenever a checkbox or the conflict preview changes in the importer; package
		// tallies and cached folder states recompute lazily when their version no longer matches.
		private int importerCheckVersion;

		// Currently highlighted row (importer only) so the user can trace a row from
		// its left-side checkbox to its right-side conflict badge in a long list.
		private PackageAssetNode importerHighlightNode;

		private static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

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
			// Payloads left by a closed window, a script reload or a crash belong to no package now.
			if (loadedPackages.Count == 0 && inFlightLoads.Count == 0)
				ImportSession.DeleteStaging(ProjectRoot, ImportSession.StagingRoot(ProjectRoot));
		}

		private void DrawImporterTab()
		{
			EnsureImporterInit();

			DrawImporterToolbar();

			GUILayout.Space(4);
			BeginTreeGUI();

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
			if (Event.current.type == EventType.Repaint)
			{
				// Rows were sliced with the previous height; a resized view repaints to fill in.
				float height = GUILayoutUtility.GetLastRect().height;
				if (Mathf.Abs(height - importerViewHeight) >= 1f)
				{
					importerViewHeight = height;
					Repaint();
				}
			}

			EditorGUILayout.EndVertical();

			HandleListAreaDrop(listArea);

			if (removeIndex >= 0)
				RemovePackage(removeIndex);
		}

		private void RemovePackage(int index)
		{
			LoadedPackage removed = loadedPackages[index];
			loadedPackages.RemoveAt(index);
			ImportSession.DeleteStaging(ProjectRoot, removed.PayloadDirectory);
			importerHighlightNode = null;
			ForgetTreeRows(removed.Tree);
			RefreshImporterConflictPreview(false);
		}

		private void DrawImporterEmptyHint()
		{
			int pending = inFlightLoads.Count;
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
				CancelPendingLoads();
				DeleteLoadedPayloads();
				loadedPackages.Clear();
				importerProjectSnapshot = null;
				importerContentUnknownPaths.Clear();
				importerHighlightNode = null;
				previewRequested = false;
				previewGeneration++;
				ForgetTreeRows();
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

			using (new EditorGUI.DisabledScope(loadedPackages.Count == 0 || inFlightLoads.Count > 0 || importQueued))
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

			string projectRoot = ProjectRoot;
			List<PendingLoad> toLoad = new List<PendingLoad>();
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
				if (already || inFlightLoads.ContainsKey(canonicalPath))
					continue;

				var load = new PendingLoad
				{
					Path = canonicalPath,
					ProjectRoot = projectRoot,
					PayloadDirectory = Path.Combine(ImportSession.StagingRoot(projectRoot), System.Guid.NewGuid().ToString("N").Substring(0, 12)),
					DropOrder = nextPackageDropOrder++,
					Generation = loadGeneration
				};
				try { load.FileLength = new FileInfo(canonicalPath).Length; }
				catch (System.Exception) { }
				inFlightLoads.Add(canonicalPath, load);
				StartLoadProgress(load);
				toLoad.Add(load);
			}

			if (toLoad.Count == 0)
				return;

			foreach (PendingLoad pending in toLoad)
			{
				PendingLoad captured = pending;
				Task.Run(() => LoadPackageWorker(captured));
			}

			HookPoll();
		}

		// Shows in Unity's background tasks, where Cancel stops the read at its next block.
		private void StartLoadProgress(PendingLoad load)
		{
			load.ProgressId = UnityEditor.Progress.Start(Path.GetFileName(load.Path), L("yspReadingPackage", "Reading package"));
			UnityEditor.Progress.RegisterCancelCallback(load.ProgressId, () =>
			{
				load.Cancelled = true;
				return true;
			});
		}

		private static void RemoveLoadProgress(PendingLoad load)
		{
			if (load.ProgressId >= 0 && UnityEditor.Progress.Exists(load.ProgressId))
				UnityEditor.Progress.Remove(load.ProgressId);
			load.ProgressId = -1;
		}

		// Runs off the main thread and performs archive/file IO only; no Unity API calls.
		private void LoadPackageWorker(PendingLoad load)
		{
			// A load cancelled while it waits for a free slot ends without reading.
			while (!loadSemaphore.Wait(100))
			{
				if (load.Cancelled)
				{
					loadErrorQueue.Enqueue(new PackageLoadError { Path = load.Path, Generation = load.Generation, Cancelled = true });
					return;
				}
			}
			string payloadDirectory = load.PayloadDirectory;
			try
			{
				if (load.Cancelled)
					throw new System.OperationCanceledException();
				var before = new FileInfo(load.Path);
				long length = before.Length;
				long lastWriteTicks = before.LastWriteTimeUtc.Ticks;
				System.Action<long> progress = bytes => Interlocked.Exchange(ref load.BytesRead, bytes);
				System.Func<bool> cancelled = () => load.Cancelled;

				// The same pass writes every payload, so Import only has to move files.
				List<UnityPackageEntry> entries;
				try
				{
					entries = UnityPackageReader.ReadMetadata(load.Path, progress, cancelled, payloadDirectory);
				}
				catch (IOException)
				{
					// Without room for the payloads beside the project, read the metadata alone;
					// Import then reads the selected files again.
					ImportSession.DeleteStaging(load.ProjectRoot, payloadDirectory);
					payloadDirectory = null;
					Interlocked.Exchange(ref load.BytesRead, 0);
					entries = UnityPackageReader.ReadMetadata(load.Path, progress, cancelled);
				}

				var after = new FileInfo(load.Path);
				if (after.Length != length || after.LastWriteTimeUtc.Ticks != lastWriteTicks)
					throw new InvalidDataException("The package changed while it was being read: " + load.Path);

				PackageAssetNode tree = PackageAssetNode.BuildProjectTree(entries);
				// Sort once off the main thread so per-frame drawing never re-sorts.
				tree.SortChildrenRecursive();

				LoadedPackage pkg = new LoadedPackage
				{
					FilePath = load.Path,
					DropOrder = load.DropOrder,
					LoadGeneration = load.Generation,
					FileLength = length,
					FileLastWriteUtcTicks = lastWriteTicks,
					PayloadDirectory = payloadDirectory,
					Entries = entries,
					Tree = tree,
					LeafCount = tree.CountLeaves(),
					IsExpanded = true
				};

				// A cancel, Clear or close after the last block still discards it with its payloads.
				if (load.Cancelled)
					throw new System.OperationCanceledException();
				loadedQueue.Enqueue(pkg);
			}
			catch (System.OperationCanceledException)
			{
				ImportSession.DeleteStaging(load.ProjectRoot, payloadDirectory);
				loadErrorQueue.Enqueue(new PackageLoadError { Path = load.Path, Generation = load.Generation, Cancelled = true });
			}
			catch (System.Exception ex)
			{
				ImportSession.DeleteStaging(load.ProjectRoot, payloadDirectory);
				loadErrorQueue.Enqueue(new PackageLoadError
				{
					Path = load.Path,
					Error = ex.ToString(),
					Generation = load.Generation
				});
			}
			finally
			{
				loadSemaphore.Release();
			}
		}

		// Invalidates in-flight reads: they stop at their next block, and any late result is
		// discarded with its payloads.
		private void CancelPendingLoads()
		{
			loadGeneration++;
			foreach (PendingLoad load in inFlightLoads.Values)
			{
				load.Cancelled = true;
				RemoveLoadProgress(load);
			}
			inFlightLoads.Clear();
			while (loadedQueue.TryDequeue(out LoadedPackage stale))
				ImportSession.DeleteStaging(ProjectRoot, stale.PayloadDirectory);
			while (loadErrorQueue.TryDequeue(out _)) { }
		}

		private void DeleteLoadedPayloads()
		{
			string projectRoot = ProjectRoot;
			foreach (LoadedPackage package in loadedPackages)
			{
				ImportSession.DeleteStaging(projectRoot, package.PayloadDirectory);
				package.PayloadDirectory = null;
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
			// A closed window or a script reload loses its packages, so their payloads go too.
			CancelPendingLoads();
			DeleteLoadedPayloads();
			if (pollHooked)
			{
				EditorApplication.update -= PollLoadQueue;
				pollHooked = false;
			}
			UnhookPreview();
			previewRequested = false;
			importerPreviewTask = null;
			previewOwners = null;
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
			// A preview still hashing project files would compete with the import's own reads and
			// moves, so the import starts once it has finished.
			if (importerPreviewTask != null && !importerPreviewTask.IsCompleted)
			{
				EditorApplication.delayCall += RunQueuedImport;
				return;
			}
			try
			{
				if (loadedPackages.Count == 0 || inFlightLoads.Count > 0)
					return;
				// The import plans again from a fresh snapshot; a preview result still pending is stale.
				previewGeneration++;
				ImportRunResult importResult = ImportSession.Apply(loadedPackages, importerPolicy);
				if (importResult.ShouldClear)
				{
					DeleteLoadedPayloads();
					loadedPackages.Clear();
					importerScroll = Vector2.zero;
					importerHighlightNode = null;
					importerProjectSnapshot = null;
					importerContentUnknownPaths.Clear();
					ForgetTreeRows();
				}
				else if (importResult.Status != ImportRunStatus.NothingSelected)
				{
					// A successful import opens nothing; this lists what failed and why.
					SmartPackageResultWindow.Show(importResult);
					// Files that were written changed what the review compares against.
					RefreshImporterConflictPreview(true);
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

			foreach (PendingLoad load in inFlightLoads.Values)
			{
				if (load.ProgressId >= 0 && load.FileLength > 0 && UnityEditor.Progress.Exists(load.ProgressId))
					UnityEditor.Progress.Report(load.ProgressId,
						Mathf.Clamp01((float)((double)Interlocked.Read(ref load.BytesRead) / load.FileLength)));
			}

			while (loadedQueue.TryDequeue(out LoadedPackage pkg))
			{
				PendingLoad load = null;
				bool current = pkg.LoadGeneration == loadGeneration && inFlightLoads.TryGetValue(pkg.FilePath, out load);
				if (current)
				{
					inFlightLoads.Remove(pkg.FilePath);
					RemoveLoadProgress(load);
					changed = true;
				}
				// A cancel that arrived after the worker's last check still discards it.
				if (!current || load.Cancelled)
				{
					ImportSession.DeleteStaging(ProjectRoot, pkg.PayloadDirectory);
					continue;
				}
				bool already = false;
				foreach (LoadedPackage existing in loadedPackages)
				{
					if (existing.FilePath == pkg.FilePath) { already = true; break; }
				}
				if (already)
					ImportSession.DeleteStaging(ProjectRoot, pkg.PayloadDirectory);
				else
					loadedPackages.Add(pkg);
				loadedPackages.Sort((a, b) => a.DropOrder.CompareTo(b.DropOrder));
				changed = true;
			}

			while (loadErrorQueue.TryDequeue(out PackageLoadError err))
			{
				if (err.Generation != loadGeneration || !inFlightLoads.TryGetValue(err.Path, out PendingLoad load))
					continue;
				inFlightLoads.Remove(err.Path);
				RemoveLoadProgress(load);
				if (!err.Cancelled)
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

			if (inFlightLoads.Count == 0 && loadedQueue.IsEmpty && loadErrorQueue.IsEmpty)
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

			string fileName = Path.GetFileName(pkg.FilePath);
			var label = new GUIContent(string.Format("{0}   ({1} / {2})", fileName, pkg.SelectedCount, pkg.LeafCount), pkg.FilePath);
			// Counts and ✕ sit at the right; a long name is clipped before them instead of running under them.
			Rect header = GUILayoutUtility.GetRect(0f, RowHeight, GUILayout.ExpandWidth(true));
			const float removeWidth = 22f;
			if (GUI.Button(new Rect(header.xMax - removeWidth, header.y + 1f, removeWidth, RowHeight - 2f), "✕", EditorStyles.miniButton))
				remove = true;
			var talliesArea = new Rect(header.x, header.y, header.width - removeWidth - 4f, header.height);
			float tallies = DrawPackageConflictTallies(pkg, talliesArea);
			float nameWidth = Mathf.Max(0f, talliesArea.width - tallies);
			float labelWidth = EditorGUIUtility.labelWidth;
			// A foldout clips its label at the label width, so give it all the room left of the counts.
			EditorGUIUtility.labelWidth = nameWidth;
			bool expanded = EditorGUI.Foldout(new Rect(header.x, header.y, nameWidth, header.height),
				pkg.IsExpanded, label, true, ClippedFoldout);
			EditorGUIUtility.labelWidth = labelWidth;
			// The header foldout now has the keys; a highlighted row must not take them.
			if (expanded != pkg.IsExpanded)
			{
				pkg.IsExpanded = expanded;
				treeKeyboardFocus = false;
			}

			if (pkg.IsExpanded && pkg.Tree != null)
				DrawVirtualTree(pkg.Tree, pkg.ConflictByPath);

			EditorGUILayout.EndVertical();
			return remove;
		}

		private void SetAllPackagesExpanded(bool expanded)
		{
			for (int i = 0; i < loadedPackages.Count; i++)
				loadedPackages[i].IsExpanded = expanded;
			treeKeyboardFocus = false;
		}

		private void RefreshImporterConflictPreview(bool refreshProjectSnapshot)
		{
			importerCheckVersion++;
			if (loadedPackages.Count == 0)
				return;
			previewSnapshotDirty |= refreshProjectSnapshot;
			previewRequested = true;
			previewGeneration++;
			previewDueAt = EditorApplication.timeSinceStartup + 0.08;
			HookPreview();
			Repaint();
		}

		private void HookPreview()
		{
			if (previewHooked) return;
			previewHooked = true;
			EditorApplication.update += UpdateImporterPreview;
		}

		private void UnhookPreview()
		{
			if (!previewHooked) return;
			previewHooked = false;
			EditorApplication.update -= UpdateImporterPreview;
		}

		private void UpdateImporterPreview()
		{
			if (importerPreviewTask != null)
			{
				if (!importerPreviewTask.IsCompleted)
					return;
				Task<ImportPlan> finished = importerPreviewTask;
				Dictionary<LoadedPackage, LoadedPackage> owners = previewOwners;
				importerPreviewTask = null;
				previewOwners = null;
				if (finished.IsFaulted)
					Debug.LogWarning("[YSP] Could not check the selected files against the project:\n"
						+ finished.Exception?.GetBaseException());
				else if (finished.Status == TaskStatus.RanToCompletion && runningPreviewGeneration == previewGeneration)
					ApplyImporterConflictPreview(finished.Result, owners);
			}

			if (previewRequested && EditorApplication.timeSinceStartup >= previewDueAt && !importQueued
				&& !EditorApplication.isCompiling && !EditorApplication.isUpdating)
				StartImporterPreview();

			if (importerPreviewTask == null && !previewRequested)
				UnhookPreview();
		}

		private void StartImporterPreview()
		{
			previewRequested = false;
			if (loadedPackages.Count == 0)
				return;
			if (previewSnapshotDirty || importerProjectSnapshot == null)
			{
				// Content fingerprints are loaded lazily and cached only for paths the
				// packages actually touch. This lets the UI distinguish Identical from a
				// real conflict without hashing the entire project. Unity APIs stay on the
				// main thread; the worker reads and hashes the targets.
				importerProjectSnapshot = SnapshotAssetProbe.Capture(
					includeContent: true,
					contentUnknownPaths: importerContentUnknownPaths);
				previewSnapshotDirty = false;
			}

			var inputs = new List<LoadedPackage>(loadedPackages.Count);
			var owners = new Dictionary<LoadedPackage, LoadedPackage>();
			foreach (LoadedPackage original in loadedPackages)
			{
				// Copy the checked leaves now: the user may keep clicking while the worker plans.
				var copy = new LoadedPackage
				{
					FilePath = original.FilePath,
					DropOrder = original.DropOrder,
					Entries = new List<UnityPackageEntry>(original.Entries),
					Tree = new PackageAssetNode { IsFolder = true }
				};
				foreach (PackageAssetNode leaf in original.Tree.EnumerateCheckedLeaves())
					copy.Tree.Children.Add(new PackageAssetNode
					{
						FullPath = leaf.FullPath,
						IsFolder = leaf.IsFolder,
						HasPackageEntry = leaf.HasPackageEntry
					});
				inputs.Add(copy);
				owners.Add(copy, original);
			}

			SnapshotAssetProbe probe = importerProjectSnapshot;
			ConflictPolicy policy = importerPolicy;
			runningPreviewGeneration = previewGeneration;
			previewOwners = owners;
			importerPreviewTask = Task.Run(() => ImportPlanBuilder.Build(inputs, probe, policy));
		}

		private void ApplyImporterConflictPreview(ImportPlan preview, Dictionary<LoadedPackage, LoadedPackage> owners)
		{
			for (int i = 0; i < loadedPackages.Count; i++)
			{
				LoadedPackage package = loadedPackages[i];
				package.ConflictByGuid.Clear();
				package.ConflictByPath.Clear();
				package.TallyVersion = -1;
			}

			for (int i = 0; i < preview.OrderedItems.Count; i++)
			{
				ImportPlanItem item = preview.OrderedItems[i];
				if (item.Package == null || item.Entry == null || !owners.TryGetValue(item.Package, out LoadedPackage owner))
					continue;
				owner.ConflictByGuid[item.Entry.Guid] = item.Conflict;
				owner.ConflictByPath[item.IncomingPath] = item.Conflict;
			}

			importerCheckVersion++;
			Repaint();
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

			int guid = 0, path = 0, update = 0, duplicate = 0, blocked = 0, selected = 0;
			AggregateConflicts(pkg.Tree, pkg.ConflictByPath, ref guid, ref path, ref update, ref duplicate, ref blocked, ref selected);
			pkg.GuidConflictCount = guid;
			pkg.PathConflictCount = path;
			pkg.UpdateCount = update;
			pkg.DuplicateCount = duplicate;
			pkg.BlockedCount = blocked;
			pkg.SelectedCount = selected;
		}

		private void AggregateConflicts(PackageAssetNode node, Dictionary<string, ImportConflict> map,
			ref int guid, ref int path, ref int update, ref int duplicate, ref int blocked, ref int selected)
		{
			if (!node.IsFolder || (node.IsFolder && node.Children.Count == 0 && node.HasPackageEntry))
			{
				node.HasCheckedGuidConflict = false;
				node.HasCheckedPathConflict = false;
				node.HasCheckedBlocked = false;
				node.HasCheckedUpdate = false;
				node.HasCheckedDuplicate = false;
				if (node.IsChecked)
				{
					selected++;
					if (map != null && map.TryGetValue(node.FullPath, out ImportConflict c))
					{
						if (c.Kind == ImportConflictKind.GuidConflict) { guid++; node.HasCheckedGuidConflict = true; }
						else if (c.Kind == ImportConflictKind.PathConflict) { path++; node.HasCheckedPathConflict = true; }
						else if (c.Kind == ImportConflictKind.Blocked) { blocked++; node.HasCheckedBlocked = true; }
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

			bool folderGuid = false, folderPath = false, folderBlocked = false, folderUpdate = false, folderDuplicate = false;
			List<PackageAssetNode> children = node.Children;
			for (int i = 0; i < children.Count; i++)
			{
				PackageAssetNode child = children[i];
				AggregateConflicts(child, map, ref guid, ref path, ref update, ref duplicate, ref blocked, ref selected);
				folderGuid |= child.HasCheckedGuidConflict;
				folderPath |= child.HasCheckedPathConflict;
				folderBlocked |= child.HasCheckedBlocked;
				folderUpdate |= child.HasCheckedUpdate;
				folderDuplicate |= child.HasCheckedDuplicate;
			}
			// A non-empty folder can also carry its own package .meta record. It is
			// implicitly selected whenever at least one descendant is selected, so expose
			// its conflict on the folder badge without inflating the user-facing file count.
			if (node.HasPackageEntry && GetTreeState(node) != PackageAssetNode.ToggleState.Unchecked
				&& map != null && map.TryGetValue(node.FullPath, out ImportConflict folderConflict))
			{
				folderGuid |= folderConflict.Kind == ImportConflictKind.GuidConflict;
				folderPath |= folderConflict.Kind == ImportConflictKind.PathConflict;
				folderBlocked |= folderConflict.Kind == ImportConflictKind.Blocked;
				folderUpdate |= folderConflict.Kind == ImportConflictKind.Update;
				folderDuplicate |= folderConflict.Kind == ImportConflictKind.Duplicate
					&& !folderConflict.ExistingFromProject;
				// A blocked folder stops the whole import, so the header counts it too.
				if (folderConflict.Kind == ImportConflictKind.Blocked)
					blocked++;
			}
			node.HasCheckedGuidConflict = folderGuid;
			node.HasCheckedPathConflict = folderPath;
			node.HasCheckedBlocked = folderBlocked;
			node.HasCheckedUpdate = folderUpdate;
			node.HasCheckedDuplicate = folderDuplicate;
		}

		private static GUIStyle s_ClippedFoldout;
		private static GUIStyle ClippedFoldout => s_ClippedFoldout
			?? (s_ClippedFoldout = new GUIStyle(EditorStyles.foldout) { clipping = TextClipping.Clip });

		// Always-visible counts on the card header (independent of foldout), drawn right
		// to left; returns the width they take from the header.
		private float DrawPackageConflictTallies(LoadedPackage pkg, Rect header)
		{
			float left = header.xMax;
			if (pkg.DuplicateCount > 0)
				left = DrawTallyBadge(string.Format(L("yspIdenticalCount", "{0} Identical"), pkg.DuplicateCount), IdenticalColor, left, header.y);
			if (pkg.UpdateCount > 0)
				left = DrawTallyBadge(string.Format(L("yspUpdateCount", "{0} Update"), pkg.UpdateCount), UpdateColor, left, header.y);
			if (pkg.GuidConflictCount > 0)
				left = DrawTallyBadge(string.Format(L("yspGuidConflictCount", "{0} GUID Conflict"), pkg.GuidConflictCount), ConflictColor, left, header.y);
			if (pkg.PathConflictCount > 0)
				left = DrawTallyBadge(string.Format(L("yspPathConflictCount", "{0} Path Taken"), pkg.PathConflictCount), ConflictColor, left, header.y);
			if (pkg.BlockedCount > 0)
				left = DrawTallyBadge(string.Format(L("yspBlockedCount", "{0} Blocked"), pkg.BlockedCount), ConflictColor, left, header.y);
			return header.xMax - left;
		}

		private static float DrawTallyBadge(string label, Color background, float right, float y)
		{
			var content = new GUIContent(label);
			float width = EditorStyles.miniLabel.CalcSize(content).x + 12f;
			var rect = new Rect(right - width, y + (RowHeight - 16f) * 0.5f, width, 16f);
			if (Event.current.type == EventType.Repaint)
			{
				EditorGUI.DrawRect(rect, background);
				GUI.Label(rect, content, EditorStyles.whiteMiniLabel);
			}
			return rect.x - 3f;
		}
	}
}
