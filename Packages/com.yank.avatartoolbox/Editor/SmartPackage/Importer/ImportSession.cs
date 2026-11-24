using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace YanK
{
	public enum ImportRunStatus
	{
		Succeeded,
		NothingSelected,
		PreflightFailed,
		PartiallyFailed
	}

	public struct ImportFailure
	{
		public string Path;
		public string Reason;
	}

	public sealed class ImportRunResult
	{
		public ImportRunStatus Status;
		public int Written;
		public int Updated;
		public int IdenticalSkipped;
		public int PolicySkipped;
		public int Failed;
		// What failed and why; Failed counts these.
		public readonly List<ImportFailure> Failures = new List<ImportFailure>();
		// Targets that now hold the package's content, for Show in Project.
		public readonly List<string> ImportedPaths = new List<string>();
		public int Skipped => IdenticalSkipped + PolicySkipped;
		public bool ShouldClear => Status == ImportRunStatus.Succeeded;
	}

	public static class ImportSession
	{
		private static bool hasConflictWindowPosition;
		private static Rect conflictWindowPosition;

		public static void ResetConflictWindowPosition()
		{
			hasConflictWindowPosition = false;
			conflictWindowPosition = default;
		}

		private static string L(string key, string defaultValue)
		{
			return YanKLocalization.L(key, defaultValue);
		}

		// Payloads live in a short folder inside the project's Temp, on the same drive as the
		// targets so they can be moved into place. Unity also clears Temp when it closes.
		public static string StagingRoot(string projectRoot)
		{
			return Path.Combine(projectRoot, "Temp", "YanKSmartPackage");
		}

		// Deletes a folder this importer made under its staging root, without following links.
		public static void DeleteStaging(string projectRoot, string directory)
		{
			if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(projectRoot))
				return;
			string root = Path.GetFullPath(StagingRoot(projectRoot)).TrimEnd(Path.DirectorySeparatorChar);
			string path = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
			if (path != root && !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
				return;
			DeleteOwnedTree(path);
		}

		private enum ConflictDecision
		{
			Skip,
			Overwrite,
			SkipAll,
			OverwriteAll
		}

		private sealed class ConflictDecisionWindow : EditorWindow
		{
			private string conflictMessage;
			private ConflictDecision decision = ConflictDecision.Skip;
			private Vector2 scroll;

			public static ConflictDecision ShowDialog(string message)
			{
				ConflictDecisionWindow window = CreateInstance<ConflictDecisionWindow>();
				window.titleContent = new GUIContent(L("yspConflictWindowTitle", "Smart Package Conflict"));
				window.conflictMessage = message;
				window.minSize = new Vector2(520f, 270f);
				window.maxSize = new Vector2(760f, 420f);
				if (hasConflictWindowPosition)
				{
					window.position = conflictWindowPosition;
				}
				else
				{
					Rect main = EditorGUIUtility.GetMainWindowPosition();
					const float width = 640f;
					const float height = 330f;
					window.position = new Rect(
						main.x + (main.width - width) * 0.5f,
						main.y + (main.height - height) * 0.5f,
						width,
						height);
				}
				window.ShowModalUtility();
				return window.decision;
			}

			private void OnGUI()
			{
				if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
				{
					Choose(ConflictDecision.Skip);
					GUIUtility.ExitGUI();
					return;
				}

				ConflictDecision? selected = null;

				GUILayout.Space(8f);
				EditorGUILayout.LabelField(L("yspConflictHeading", "Import conflict"), EditorStyles.boldLabel);
				GUILayout.Space(4f);
				scroll = EditorGUILayout.BeginScrollView(scroll, EditorStyles.helpBox);
				EditorGUILayout.LabelField(conflictMessage ?? string.Empty, EditorStyles.wordWrappedLabel);
				EditorGUILayout.EndScrollView();
				GUILayout.Space(8f);

				EditorGUILayout.LabelField(L("yspConflictThis", "This conflict"), EditorStyles.miniBoldLabel);
				EditorGUILayout.BeginHorizontal();
				if (GUILayout.Button(L("yspOverwrite", "Overwrite"), GUILayout.Height(28f)))
					selected = ConflictDecision.Overwrite;
				if (GUILayout.Button(L("yspSkip", "Skip"), GUILayout.Height(28f)))
					selected = ConflictDecision.Skip;
				EditorGUILayout.EndHorizontal();

				EditorGUILayout.LabelField(L("yspConflictRemaining", "This and all remaining conflicts"), EditorStyles.miniBoldLabel);
				EditorGUILayout.BeginHorizontal();
				if (GUILayout.Button(L("yspOverwriteAll", "Overwrite All"), GUILayout.Height(28f)))
					selected = ConflictDecision.OverwriteAll;
				if (GUILayout.Button(L("yspSkipAll", "Skip All"), GUILayout.Height(28f)))
					selected = ConflictDecision.SkipAll;
				EditorGUILayout.EndHorizontal();
				GUILayout.Space(8f);

				if (selected.HasValue)
				{
					Choose(selected.Value);
					GUIUtility.ExitGUI();
				}
			}

			private void Choose(ConflictDecision selected)
			{
				decision = selected;
				RememberPosition();
				Close();
			}

			private void OnDisable()
			{
				RememberPosition();
			}

			private void RememberPosition()
			{
				if (position.width <= 0f || position.height <= 0f)
					return;
				conflictWindowPosition = position;
				hasConflictWindowPosition = true;
			}
		}

		public static ImportRunResult Apply(IEnumerable<LoadedPackage> packages, ConflictPolicy policy)
		{
			var result = new ImportRunResult();
			if (packages == null)
			{
				result.Status = ImportRunStatus.NothingSelected;
				return result;
			}

			var packageList = new List<LoadedPackage>();
			foreach (LoadedPackage package in packages)
				if (package != null)
					packageList.Add(package);

			bool skipAll = false;
			bool overwriteAll = false;
			// Hash only the project targets touched by this final plan. This lets identical
			// project content skip before Ask dialogs, while the continuously refreshed UI
			// preview remains lightweight.
			SnapshotAssetProbe.SaveDirtyTargets(packageList, out HashSet<string> contentUnknownPaths);
			SnapshotAssetProbe snapshot = SnapshotAssetProbe.Capture(
				includeContent: true,
				contentUnknownPaths: contentUnknownPaths);
			string projectRoot = Directory.GetParent(Application.dataPath).FullName;

			// A blocked entry stops the whole run, so find that out before asking anything.
			ImportPlan preflight = ImportPlanBuilder.Build(packageList, snapshot, policy);
			if (preflight.OrderedItems.Count > 0 && !preflight.CanApply)
				return Blocked(result, projectRoot, preflight);

			ImportPlan plan = ImportPlanBuilder.Build(packageList, snapshot, policy, item =>
			{
				if (overwriteAll)
					return true;
				if (skipAll)
					return false;
				ImportConflict conflict = item.Conflict;
				string incomingGuid = string.IsNullOrEmpty(item.Entry.IdentityGuid)
					? L("yspConflictPathManaged", "(path-managed)")
					: item.Entry.IdentityGuid;
				string existingGuid = string.IsNullOrEmpty(conflict.ExistingGuid)
					? L("yspConflictNone", "(none)")
					: conflict.ExistingGuid;
				string message = string.Format(
					L("yspConflictDetails", "Kind: {0}\nIncoming: {1}\nIncoming GUID: {2}\nTarget: {3}\nExisting GUID: {4}"),
					ImportConflictLabels.Kind(conflict.Kind),
					item.IncomingPath,
					incomingGuid,
					item.TargetPath,
					existingGuid);
				ConflictDecision decision = ConflictDecisionWindow.ShowDialog(message);
				if (decision == ConflictDecision.OverwriteAll)
					overwriteAll = true;
				else if (decision == ConflictDecision.SkipAll)
					skipAll = true;
				return decision == ConflictDecision.Overwrite || decision == ConflictDecision.OverwriteAll;
			});

			if (plan.OrderedItems.Count == 0)
			{
				EditorUtility.DisplayDialog(
					L("yspImportDialogTitle", "Smart Package Import"),
					L("yspNothingSelected", "Nothing selected."),
					L("yspOK", "OK"));
				result.Status = ImportRunStatus.NothingSelected;
				return result;
			}

			if (!plan.CanApply)
				return Blocked(result, projectRoot, plan);

			string runName = "import-" + Guid.NewGuid().ToString("N").Substring(0, 8);
			string runDirectory = Path.Combine(StagingRoot(projectRoot), runName);
			// Originals stay recoverable across a restart if a rollback ever fails.
			string backupDirectory = Path.Combine(projectRoot, "Library", "YanKSmartPackage", runName);
			var writtenItems = new List<ImportPlanItem>();
			var failedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var staged = new Dictionary<LoadedPackage, Dictionary<string, StagedAssetPayload>>();
			var packageErrors = new Dictionary<LoadedPackage, string>();
			Stopwatch clock = Stopwatch.StartNew();
			long nextProgress = 0;
			try
			{
				for (int i = 0; i < plan.OrderedItems.Count; i++)
				{
					ImportPlanItem item = plan.OrderedItems[i];
					if (item.Action == ImportPlanAction.SkipByPolicy)
					{
						result.PolicySkipped++;
						continue;
					}
					if (item.Action == ImportPlanAction.SkipIdentical)
					{
						// It matched a copy an earlier package was meant to write.
						if (failedTargets.Contains(item.TargetPath))
						{
							AddFailure(result, projectRoot, item.TargetPath, "Its matching copy from an earlier package was not imported.");
							continue;
						}
						result.IdenticalSkipped++;
						result.ImportedPaths.Add(item.TargetPath);
						continue;
					}
					if (item.Action != ImportPlanAction.WriteNew && item.Action != ImportPlanAction.Overwrite)
						continue;

					// A progress bar per file can take longer than writing a small file.
					if (clock.ElapsedMilliseconds >= nextProgress)
					{
						nextProgress = clock.ElapsedMilliseconds + 100;
						EditorUtility.DisplayProgressBar(
							L("yspImportProgressTitle", "Importing…"),
							string.Format(
								L("yspImportProgressMessage", "{0} / {1}  {2}"),
								i + 1,
								plan.OrderedItems.Count,
								item.TargetPath),
							(float)(i + 1) / plan.OrderedItems.Count);
					}

					// One failed file does not stop the others; each failure is listed with why.
					try
					{
						Dictionary<string, StagedAssetPayload> payloads = PayloadsFor(item.Package, plan, runDirectory, staged, packageErrors);
						if (!payloads.TryGetValue(item.Entry.Guid, out StagedAssetPayload payload))
						{
							// Its staged copy was gone and the package could not be read again.
							if (packageErrors.TryGetValue(item.Package, out string packageError))
								throw new IOException(packageError);
							// A pathname-only folder (an exported package root) has no members.
							if (item.Entry.HasAssetMember || item.Entry.HasMetaMember)
								throw new InvalidDataException("Selected payload is missing for " + item.IncomingPath);
							payload = new StagedAssetPayload();
						}
						ValidatePayload(item.Entry, payload);
						if (!ProjectPackagePath.TryGetAbsolutePath(projectRoot, item.TargetPath,
							out string absolutePath, out string pathError))
							throw new InvalidDataException(pathError);

						if (IsIdentical(item.Entry, absolutePath))
						{
							result.IdenticalSkipped++;
							result.ImportedPaths.Add(item.TargetPath);
							continue;
						}

						WriteEntry(item.Entry, payload, absolutePath, backupDirectory);
						writtenItems.Add(item);
						result.ImportedPaths.Add(item.TargetPath);
						if (item.Action == ImportPlanAction.WriteNew)
							result.Written++;
						else
							result.Updated++;
					}
					catch (Exception ex)
					{
						failedTargets.Add(item.TargetPath);
						AddFailure(result, projectRoot, item.TargetPath, ex.Message);
						Debug.LogError("[YSP] Import failed for " + item.IncomingPath + ": " + ex);
					}
				}
			}
			finally
			{
				EditorUtility.ClearProgressBar();
				DeleteStaging(projectRoot, runDirectory);
				// A failed rollback leaves its originals here, named in that file's reason.
				DeleteIfEmpty(backupDirectory);
				DeleteIfEmpty(Path.GetDirectoryName(backupDirectory));
			}

			if (writtenItems.Count > 0)
			{
				AssetDatabase.Refresh(ImportAssetOptions.Default);
				VerifyWrittenGuids(writtenItems, result, projectRoot);
			}

			result.Status = result.Failed > 0 ? ImportRunStatus.PartiallyFailed : ImportRunStatus.Succeeded;
			return result;
		}

		private static ImportRunResult Blocked(ImportRunResult result, string projectRoot, ImportPlan plan)
		{
			foreach (ImportPlanItem item in plan.OrderedItems)
				if (item.Action == ImportPlanAction.Blocked)
					AddFailure(result, projectRoot, item.IncomingPath, item.Conflict.Message);
			result.Status = ImportRunStatus.PreflightFailed;
			return result;
		}

		private static void AddFailure(ImportRunResult result, string projectRoot, string path, string reason)
		{
			result.Failed++;
			result.Failures.Add(new ImportFailure { Path = path, Reason = ProjectRelative(projectRoot, reason) });
		}

		// Reasons name project files the way the list does, relative to the project.
		private static string ProjectRelative(string projectRoot, string reason)
		{
			if (string.IsNullOrEmpty(reason) || string.IsNullOrEmpty(projectRoot))
				return reason;
			string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			return reason.Replace(root + Path.DirectorySeparatorChar, string.Empty)
				.Replace(root.Replace('\\', '/') + "/", string.Empty);
		}

		private static Dictionary<string, StagedAssetPayload> PayloadsFor(
			LoadedPackage package,
			ImportPlan plan,
			string runDirectory,
			Dictionary<LoadedPackage, Dictionary<string, StagedAssetPayload>> staged,
			Dictionary<LoadedPackage, string> packageErrors)
		{
			if (staged.TryGetValue(package, out Dictionary<string, StagedAssetPayload> payloads))
				return payloads;
			var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (ImportPlanItem item in plan.OrderedItems)
				if (item.Package == package
					&& (item.Action == ImportPlanAction.WriteNew || item.Action == ImportPlanAction.Overwrite))
					wanted.Add(item.Entry.Guid);

			// Loading already wrote every payload; read the package again only for what is gone.
			var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			payloads = PreparedPayloads(package, wanted, missing);
			if (missing.Count > 0)
			{
				try
				{
					if (!PackageFileIsUnchanged(package))
						throw new IOException("Package changed after it was scanned: " + package.FilePath);
					Dictionary<string, StagedAssetPayload> restaged = UnityPackageReader.StageSelected(package.FilePath, missing,
						Path.Combine(runDirectory, "package-" + package.DropOrder));
					if (!PackageFileIsUnchanged(package))
						throw new IOException("Package changed while it was read: " + package.FilePath);
					foreach (KeyValuePair<string, StagedAssetPayload> pair in restaged)
						payloads[pair.Key] = pair.Value;
				}
				catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
				{
					// Only the files that were not staged fail; the rest still import.
					packageErrors[package] = ex.Message;
				}
			}
			staged.Add(package, payloads);
			return payloads;
		}

		private static Dictionary<string, StagedAssetPayload> PreparedPayloads(LoadedPackage package, HashSet<string> wanted, HashSet<string> missing)
		{
			var result = new Dictionary<string, StagedAssetPayload>(StringComparer.OrdinalIgnoreCase);
			if (package.Entries == null)
				return result;
			foreach (UnityPackageEntry entry in package.Entries)
			{
				if (entry == null || !wanted.Contains(entry.Guid))
					continue;
				if (string.IsNullOrEmpty(package.PayloadDirectory))
				{
					missing.Add(entry.Guid);
					continue;
				}
				var payload = new StagedAssetPayload();
				bool complete = true;
				if (entry.HasAssetMember)
				{
					var asset = new FileInfo(Path.Combine(package.PayloadDirectory, entry.Guid + ".asset.payload"));
					complete = asset.Exists && asset.Length == entry.Size;
					payload.AssetPath = asset.FullName;
					payload.AssetSize = complete ? asset.Length : 0;
				}
				if (complete && entry.HasMetaMember)
				{
					payload.MetaPath = Path.Combine(package.PayloadDirectory, entry.Guid + ".meta.payload");
					complete = File.Exists(payload.MetaPath);
				}
				if (complete)
					result[entry.Guid] = payload;
				else
					missing.Add(entry.Guid);
			}
			return result;
		}

		private static bool PackageFileIsUnchanged(LoadedPackage package)
		{
			if (package == null || string.IsNullOrEmpty(package.FilePath) || !File.Exists(package.FilePath))
				return false;
			var info = new FileInfo(package.FilePath);
			return info.Length == package.FileLength && info.LastWriteTimeUtc.Ticks == package.FileLastWriteUtcTicks;
		}

		// Staged files sat on disk while the user reviewed, so hash them once more before use.
		private static void ValidatePayload(UnityPackageEntry entry, StagedAssetPayload payload)
		{
			if (entry.HasAssetMember && string.IsNullOrEmpty(payload.AssetPath))
				throw new InvalidDataException("Package asset member is missing: " + entry.AssetPath);
			if (entry.HasMetaMember && string.IsNullOrEmpty(payload.MetaPath))
				throw new InvalidDataException("Package asset.meta member is missing: " + entry.AssetPath);
			if (entry.HasMetaMember)
			{
				byte[] meta = File.ReadAllBytes(payload.MetaPath);
				if (!string.Equals(GuidUtility.ExtractGuidFromMeta(meta), entry.MetaGuid, StringComparison.OrdinalIgnoreCase))
					throw new InvalidDataException("Package meta GUID changed after preflight: " + entry.AssetPath);
				if (!string.Equals(GuidUtility.ComputeSha256(meta), entry.MetaHash, StringComparison.Ordinal))
					throw new InvalidDataException("Package meta content changed after preflight: " + entry.AssetPath);
			}
			if (entry.HasAssetMember
				&& !string.Equals(HashFile(payload.AssetPath), entry.AssetHash, StringComparison.Ordinal))
				throw new InvalidDataException("Package asset content changed after preflight: " + entry.AssetPath);
		}

		private static bool IsIdentical(UnityPackageEntry entry, string absolutePath)
		{
			string metaPath = absolutePath + ".meta";
			if (entry.IsFolder)
			{
				if (!Directory.Exists(absolutePath))
					return false;
			}
			else
			{
				if (!File.Exists(absolutePath) || new FileInfo(absolutePath).Length != entry.Size
					|| !HashEquals(absolutePath, entry.AssetHash))
					return false;
			}
			if (entry.HasMetaMember)
				return HashEquals(metaPath, entry.MetaHash);
			return !File.Exists(metaPath);
		}

		private static bool HashEquals(string path, string expected)
		{
			if (string.IsNullOrEmpty(expected) || !File.Exists(path))
				return false;
			try
			{
				return string.Equals(HashFile(path), expected, StringComparison.Ordinal);
			}
			catch (IOException)
			{
				return false;
			}
			catch (UnauthorizedAccessException)
			{
				return false;
			}
		}

		private static string HashFile(string path)
		{
			using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
				return GuidUtility.ComputeSha256(stream);
		}

		private sealed class FileCommitPart
		{
			public string TargetPath;
			// A staged payload moved into place; null removes an existing file there.
			public string SourcePath;
			public string BackupPath;
			public bool HadOriginal;
			public bool OriginalMoved;
			public bool NewInstalled;
		}

		private static void WriteEntry(UnityPackageEntry entry, StagedAssetPayload payload, string absolutePath, string backupDirectory)
		{
			// A null source intentionally removes a stale sidecar for path-managed
			// ProjectSettings / Packages entries.
			string metaSource = entry.HasMetaMember ? payload.MetaPath : null;
			if (entry.IsFolder)
			{
				if (File.Exists(absolutePath))
					throw new IOException("A file already occupies the folder target: " + absolutePath);
				bool createdFolder = !Directory.Exists(absolutePath);
				Directory.CreateDirectory(absolutePath);
				try
				{
					CommitFileParts(backupDirectory, new FileCommitPart
					{
						TargetPath = absolutePath + ".meta",
						SourcePath = metaSource
					});
				}
				catch
				{
					if (createdFolder)
					{
						try { Directory.Delete(absolutePath, false); }
						catch (Exception cleanupError)
						{
							Debug.LogWarning("[YSP] Could not remove a newly-created folder after rollback: "
								+ absolutePath + "\n" + cleanupError);
						}
					}
					throw;
				}
				return;
			}

			if (Directory.Exists(absolutePath))
				throw new IOException("A folder already occupies the file target: " + absolutePath);
			string parent = Path.GetDirectoryName(absolutePath);
			if (!string.IsNullOrEmpty(parent))
				Directory.CreateDirectory(parent);

			CommitFileParts(backupDirectory,
				new FileCommitPart { TargetPath = absolutePath, SourcePath = payload.AssetPath },
				new FileCommitPart { TargetPath = absolutePath + ".meta", SourcePath = metaSource });
		}

		// Originals move aside into a short folder on the same drive, not beside the target:
		// a long name beside a long path would pass Windows' 260-character limit where Unity
		// itself still imports the target.
		private static void CommitFileParts(string backupDirectory, params FileCommitPart[] parts)
		{
			string token = Guid.NewGuid().ToString("N").Substring(0, 12);
			bool committed = false;
			try
			{
				for (int i = 0; i < parts.Length; i++)
				{
					FileCommitPart part = parts[i];
					if (Directory.Exists(part.TargetPath))
						throw new IOException("A folder occupies a file target: " + part.TargetPath);
					string directory = Path.GetDirectoryName(part.TargetPath);
					if (string.IsNullOrEmpty(directory))
						throw new IOException("The import target has no parent directory: " + part.TargetPath);
					Directory.CreateDirectory(directory);
					if (part.SourcePath != null && !File.Exists(part.SourcePath))
						throw new IOException("The staged payload is missing for " + part.TargetPath);
					part.HadOriginal = File.Exists(part.TargetPath);
					if (part.HadOriginal)
						part.BackupPath = Path.Combine(backupDirectory, token + "-" + i + ".bak");
				}

				// Move every original aside only after every replacement is known to exist.
				for (int i = 0; i < parts.Length; i++)
				{
					FileCommitPart part = parts[i];
					if (!part.HadOriginal)
						continue;
					Directory.CreateDirectory(backupDirectory);
					File.Move(part.TargetPath, part.BackupPath);
					part.OriginalMoved = true;
				}

				for (int i = 0; i < parts.Length; i++)
				{
					FileCommitPart part = parts[i];
					if (part.SourcePath == null)
						continue;
					File.Move(part.SourcePath, part.TargetPath);
					part.NewInstalled = true;
				}

				committed = true;
			}
			catch (Exception commitError)
			{
				var rollbackErrors = new List<string>();
				for (int i = parts.Length - 1; i >= 0; i--)
				{
					FileCommitPart part = parts[i];
					try
					{
						if (part.NewInstalled && File.Exists(part.TargetPath))
							ReturnToStaging(part);
						if (part.OriginalMoved && File.Exists(part.BackupPath))
							File.Move(part.BackupPath, part.TargetPath);
					}
					catch (Exception rollbackError)
					{
						rollbackErrors.Add(part.TargetPath + ": " + rollbackError.Message
							+ " (backup: " + part.BackupPath + ")");
					}
				}

				if (rollbackErrors.Count > 0)
					throw new IOException(
						"Import commit failed and rollback was incomplete. Recovery files were preserved:\n"
						+ string.Join("\n", rollbackErrors.ToArray()),
						commitError);
				throw new IOException(commitError.Message + " The original file and meta were restored.", commitError);
			}
			finally
			{
				if (committed)
					for (int i = 0; i < parts.Length; i++)
						TryDeleteOwnedTemporary(parts[i].BackupPath);
			}
		}

		// The payload moved in goes back to staging, so a retry can still use it.
		private static void ReturnToStaging(FileCommitPart part)
		{
			try
			{
				File.Move(part.TargetPath, part.SourcePath);
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				File.Delete(part.TargetPath);
			}
		}

		private static void TryDeleteOwnedTemporary(string path)
		{
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
				return;
			try { File.Delete(path); }
			catch (Exception cleanupError)
			{
				Debug.LogWarning("[YSP] Could not remove temporary import file: " + path + "\n" + cleanupError);
			}
		}

		private static void DeleteIfEmpty(string directory)
		{
			try
			{
				if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0)
					Directory.Delete(directory, false);
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
		}

		private static void DeleteOwnedTree(string path)
		{
			try
			{
				if (!Directory.Exists(path))
					return;
				if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
					return;
				foreach (string child in Directory.GetFileSystemEntries(path))
				{
					FileAttributes attributes = File.GetAttributes(child);
					if ((attributes & FileAttributes.ReparsePoint) != 0)
						continue;
					if ((attributes & FileAttributes.Directory) != 0)
						DeleteOwnedTree(child);
					else
						File.Delete(child);
				}
				if (Directory.GetFileSystemEntries(path).Length == 0)
					Directory.Delete(path, false);
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				Debug.LogWarning("[YSP] Could not remove temporary import files: " + path + "\n" + ex.Message);
			}
		}

		private static void VerifyWrittenGuids(List<ImportPlanItem> writtenItems, ImportRunResult result, string projectRoot)
		{
			// Multiple selected packages may intentionally replace the same target. Verify
			// only the last successful write at each path; historical GUIDs are not expected
			// to remain after a later path replacement.
			var finalByPath = new Dictionary<string, ImportPlanItem>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < writtenItems.Count; i++)
				finalByPath[writtenItems[i].TargetPath] = writtenItems[i];

			bool hasUnregisteredPackage = false;
			foreach (ImportPlanItem item in finalByPath.Values)
			{
				string expected = item.Entry.IdentityGuid;
				if (string.IsNullOrEmpty(expected))
					continue;
				// AssetDatabase does not index a new embedded package until the Package
				// Manager registers it, so there is no GUID to read back yet.
				if (IsInUnregisteredPackage(item.TargetPath))
				{
					hasUnregisteredPackage = true;
					continue;
				}
				string actual = AssetDatabase.AssetPathToGUID(item.TargetPath);
				if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
				{
					// It is listed as failed instead of imported, so the counts agree with the list.
					if (item.Action == ImportPlanAction.WriteNew)
						result.Written--;
					else
						result.Updated--;
					result.ImportedPaths.Remove(item.TargetPath);
					string message = "GUID verification failed. Expected " + expected + ", got "
						+ (string.IsNullOrEmpty(actual) ? "(none)" : actual);
					AddFailure(result, projectRoot, item.TargetPath, message);
					Debug.LogError("[YSP] GUID verification failed for " + item.TargetPath
						+ ". Expected " + expected + ", got " + (string.IsNullOrEmpty(actual) ? "(none)" : actual));
				}
			}

			if (hasUnregisteredPackage)
				UnityEditor.PackageManager.Client.Resolve();
		}

		private static bool IsInUnregisteredPackage(string path)
		{
			string[] parts = path.Split('/');
			return parts.Length > 1
				&& string.Equals(parts[0], "Packages", StringComparison.Ordinal)
				&& !AssetDatabase.IsValidFolder("Packages/" + parts[1]);
		}
	}
}
