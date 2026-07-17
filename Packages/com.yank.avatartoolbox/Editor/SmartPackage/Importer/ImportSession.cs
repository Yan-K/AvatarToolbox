using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace YanK
{
	public enum ImportRunStatus
	{
		Succeeded,
		NothingSelected,
		PreflightFailed,
		PartiallyFailed
	}

	public sealed class ImportRunResult
	{
		public ImportRunStatus Status;
		public int Written;
		public int Updated;
		public int IdenticalSkipped;
		public int PolicySkipped;
		public int Failed;
		public int NotAttempted;
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
				string conflictKind = L("yspConflictGuid", "GUID Conflict");
				string message = string.Format(
					L("yspConflictDetails", "Kind: {0}\nIncoming: {1}\nIncoming GUID: {2}\nTarget: {3}\nExisting GUID: {4}"),
					conflictKind,
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
			{
				string message = string.Format(
					L("yspImportPreflightFailed", "Import preflight failed:\n\n{0}"),
					string.Join("\n", plan.Errors.ToArray()));
				EditorUtility.DisplayDialog(
					L("yspImportDialogTitle", "Smart Package Import"),
					message,
					L("yspOK", "OK"));
				result.Status = ImportRunStatus.PreflightFailed;
				result.Failed = plan.Errors.Count;
				return result;
			}

			string projectRoot = Directory.GetParent(Application.dataPath).FullName;
			var writtenItems = new List<ImportPlanItem>();
			bool abortRemainingWrites = false;
			try
			{
				int index = 0;
				while (index < plan.OrderedItems.Count && !abortRemainingWrites)
				{
					LoadedPackage package = plan.OrderedItems[index].Package;
					int end = index + 1;
					while (end < plan.OrderedItems.Count && plan.OrderedItems[end].Package == package)
						end++;

					if (!PackageFileIsUnchanged(package))
					{
						result.Failed++;
						result.NotAttempted += CountWrites(plan.OrderedItems, index, plan.OrderedItems.Count);
						Debug.LogError("[YSP] Package changed after it was scanned: " + package.FilePath);
						abortRemainingWrites = true;
						break;
					}

					var wantedGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					for (int i = index; i < end; i++)
					{
						ImportPlanAction action = plan.OrderedItems[i].Action;
						if (action == ImportPlanAction.WriteNew || action == ImportPlanAction.Overwrite)
							wantedGuids.Add(plan.OrderedItems[i].Entry.Guid);
					}

					Dictionary<string, LoadedAssetBytes> payloads;
					try
					{
						payloads = UnityPackageReader.ReadBytesFor(package.FilePath, wantedGuids);
					}
					catch (Exception ex)
					{
						result.Failed++;
						result.NotAttempted += CountWrites(plan.OrderedItems, index, plan.OrderedItems.Count);
						Debug.LogError("[YSP] Failed to read package payload: " + ex);
						abortRemainingWrites = true;
						break;
					}

					for (int i = index; i < end; i++)
					{
						ImportPlanItem item = plan.OrderedItems[i];
						if (item.Action == ImportPlanAction.SkipByPolicy)
						{
							result.PolicySkipped++;
							continue;
						}
						if (item.Action == ImportPlanAction.SkipIdentical)
						{
							result.IdenticalSkipped++;
							continue;
						}
						if (item.Action != ImportPlanAction.WriteNew && item.Action != ImportPlanAction.Overwrite)
							continue;

						EditorUtility.DisplayProgressBar(
							L("yspImportProgressTitle", "Importing…"),
							string.Format(
								L("yspImportProgressMessage", "{0} / {1}  {2}"),
								i + 1,
								plan.OrderedItems.Count,
								item.TargetPath),
							(float)(i + 1) / plan.OrderedItems.Count);

						try
						{
							if (!payloads.TryGetValue(item.Entry.Guid, out LoadedAssetBytes payload))
								throw new InvalidDataException("Selected payload is missing for " + item.IncomingPath);
							ValidatePayload(item.Entry, payload);
							if (!ProjectPackagePath.TryGetAbsolutePath(projectRoot, item.TargetPath,
								out string absolutePath, out string pathError))
								throw new InvalidDataException(pathError);

							if (IsIdentical(item.Entry, payload, absolutePath))
							{
								result.IdenticalSkipped++;
								continue;
							}

							WriteEntry(item.Entry, payload, absolutePath);
							writtenItems.Add(item);
							if (item.Action == ImportPlanAction.WriteNew)
								result.Written++;
							else
								result.Updated++;
						}
						catch (Exception ex)
						{
							result.Failed++;
							result.NotAttempted += CountWrites(plan.OrderedItems, i + 1, plan.OrderedItems.Count);
							Debug.LogError("[YSP] Import failed for " + item.IncomingPath + ": " + ex);
							abortRemainingWrites = true;
							break;
						}
					}

					index = end;
				}
			}
			finally
			{
				EditorUtility.ClearProgressBar();
			}

			if (writtenItems.Count > 0)
			{
				AssetDatabase.Refresh(ImportAssetOptions.Default);
				VerifyWrittenGuids(writtenItems, result);
			}

			result.Status = result.Failed > 0 ? ImportRunStatus.PartiallyFailed : ImportRunStatus.Succeeded;
			string summary = string.Format(
				L("yspImportSummary", "New: {0}\nOverwritten: {1}\nSkipped: {2}\nFailed: {3}"),
				result.Written, result.Updated, result.Skipped, result.Failed);
			EditorUtility.DisplayDialog(
				L("yspImportDialogTitle", "Smart Package Import"),
				summary,
				L("yspOK", "OK"));
			return result;
		}

		private static int CountWrites(List<ImportPlanItem> items, int start, int end)
		{
			int count = 0;
			for (int i = start; i < end; i++)
			{
				ImportPlanAction action = items[i].Action;
				if (action == ImportPlanAction.WriteNew || action == ImportPlanAction.Overwrite)
					count++;
			}
			return count;
		}

		private static bool PackageFileIsUnchanged(LoadedPackage package)
		{
			if (package == null || string.IsNullOrEmpty(package.FilePath) || !File.Exists(package.FilePath))
				return false;
			var info = new FileInfo(package.FilePath);
			return info.Length == package.FileLength && info.LastWriteTimeUtc.Ticks == package.FileLastWriteUtcTicks;
		}

		private static void ValidatePayload(UnityPackageEntry entry, LoadedAssetBytes payload)
		{
			if (entry.HasAssetMember && payload.AssetBytes == null)
				throw new InvalidDataException("Package asset member is missing: " + entry.AssetPath);
			if (entry.HasMetaMember && payload.MetaBytes == null)
				throw new InvalidDataException("Package asset.meta member is missing: " + entry.AssetPath);
			if (entry.HasMetaMember)
			{
				string payloadGuid = GuidUtility.ExtractGuidFromMeta(payload.MetaBytes);
				if (!string.Equals(payloadGuid, entry.MetaGuid, StringComparison.OrdinalIgnoreCase))
					throw new InvalidDataException("Package meta GUID changed after preflight: " + entry.AssetPath);
			}
			if (entry.HasAssetMember
				&& !string.Equals(GuidUtility.ComputeSha256(payload.AssetBytes), entry.AssetHash, StringComparison.Ordinal))
				throw new InvalidDataException("Package asset content changed after preflight: " + entry.AssetPath);
			if (entry.HasMetaMember
				&& !string.Equals(GuidUtility.ComputeSha256(payload.MetaBytes), entry.MetaHash, StringComparison.Ordinal))
				throw new InvalidDataException("Package meta content changed after preflight: " + entry.AssetPath);
		}

		private static bool IsIdentical(UnityPackageEntry entry, LoadedAssetBytes payload, string absolutePath)
		{
			if (entry.IsFolder)
			{
				if (!Directory.Exists(absolutePath))
					return false;
				if (entry.HasMetaMember)
					return BytesEqualFile(payload.MetaBytes, absolutePath + ".meta");
				return !File.Exists(absolutePath + ".meta");
			}
			if (!File.Exists(absolutePath) || !BytesEqualFile(payload.AssetBytes, absolutePath))
				return false;
			if (entry.HasMetaMember)
				return BytesEqualFile(payload.MetaBytes, absolutePath + ".meta");
			return !File.Exists(absolutePath + ".meta");
		}

		private static bool BytesEqualFile(byte[] expected, string filePath)
		{
			if (expected == null || !File.Exists(filePath))
				return false;
			try
			{
				var before = new FileInfo(filePath);
				long originalLength = before.Length;
				long originalWriteTicks = before.LastWriteTimeUtc.Ticks;
				if (originalLength != expected.LongLength)
					return false;
				using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
				{
					byte[] buffer = new byte[64 * 1024];
					int offset = 0;
					while (offset < expected.Length)
					{
						int count = Math.Min(buffer.Length, expected.Length - offset);
						int read = stream.Read(buffer, 0, count);
						if (read != count)
							return false;
						for (int i = 0; i < read; i++)
							if (buffer[i] != expected[offset + i])
								return false;
						offset += read;
					}
				}
				var after = new FileInfo(filePath);
				return after.Length == originalLength
					&& after.LastWriteTimeUtc.Ticks == originalWriteTicks;
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

		private sealed class FileCommitPart
		{
			public string TargetPath;
			public byte[] Bytes;
			public string StagedPath;
			public string BackupPath;
			public bool HadOriginal;
			public bool OriginalMoved;
			public bool NewInstalled;
		}

		private static void WriteEntry(UnityPackageEntry entry, LoadedAssetBytes payload, string absolutePath)
		{
			if (entry.IsFolder)
			{
				if (File.Exists(absolutePath))
					throw new IOException("A file already occupies the folder target: " + absolutePath);
				bool createdFolder = !Directory.Exists(absolutePath);
				Directory.CreateDirectory(absolutePath);
				try
				{
					CommitFileParts(new FileCommitPart
					{
						TargetPath = absolutePath + ".meta",
						Bytes = payload.MetaBytes
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

			CommitFileParts(
				new FileCommitPart { TargetPath = absolutePath, Bytes = payload.AssetBytes },
				new FileCommitPart
				{
					TargetPath = absolutePath + ".meta",
					// A null payload intentionally removes a stale sidecar for path-managed
					// ProjectSettings / Packages files.
					Bytes = entry.HasMetaMember ? payload.MetaBytes : null
				});
		}

		private static void CommitFileParts(params FileCommitPart[] parts)
		{
			string token = Guid.NewGuid().ToString("N");
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
					part.StagedPath = Path.Combine(directory, ".ysp-import-" + token + "-" + i + ".tmp");
					part.BackupPath = Path.Combine(directory, ".ysp-import-" + token + "-" + i + ".bak");
					part.HadOriginal = File.Exists(part.TargetPath);
					if (part.Bytes != null)
					{
						using (var stream = new FileStream(part.StagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
							stream.Write(part.Bytes, 0, part.Bytes.Length);
					}
				}

				// Move every original aside only after every replacement has been staged.
				for (int i = 0; i < parts.Length; i++)
				{
					FileCommitPart part = parts[i];
					if (!part.HadOriginal)
						continue;
					File.Move(part.TargetPath, part.BackupPath);
					part.OriginalMoved = true;
				}

				for (int i = 0; i < parts.Length; i++)
				{
					FileCommitPart part = parts[i];
					if (part.Bytes == null)
						continue;
					File.Move(part.StagedPath, part.TargetPath);
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
							File.Delete(part.TargetPath);
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
				throw new IOException("Import commit failed; the original file and meta were restored.", commitError);
			}
			finally
			{
				for (int i = 0; i < parts.Length; i++)
				{
					FileCommitPart part = parts[i];
					TryDeleteOwnedTemporary(part.StagedPath);
					if (committed)
						TryDeleteOwnedTemporary(part.BackupPath);
				}
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

		private static void VerifyWrittenGuids(List<ImportPlanItem> writtenItems, ImportRunResult result)
		{
			// Multiple selected packages may intentionally replace the same target. Verify
			// only the last successful write at each path; historical GUIDs are not expected
			// to remain after a later path replacement.
			var finalByPath = new Dictionary<string, ImportPlanItem>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < writtenItems.Count; i++)
				finalByPath[writtenItems[i].TargetPath] = writtenItems[i];

			foreach (ImportPlanItem item in finalByPath.Values)
			{
				string expected = item.Entry.IdentityGuid;
				if (string.IsNullOrEmpty(expected))
					continue;
				string actual = AssetDatabase.AssetPathToGUID(item.TargetPath);
				if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
				{
					result.Failed++;
					Debug.LogError("[YSP] GUID verification failed for " + item.TargetPath
						+ ". Expected " + expected + ", got " + (string.IsNullOrEmpty(actual) ? "(none)" : actual));
				}
			}
		}
	}
}
