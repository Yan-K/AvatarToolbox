using System;
using System.Collections.Generic;

namespace YanK
{
	public enum ImportPlanAction
	{
		WriteNew,
		Overwrite,
		SkipIdentical,
		SkipByPolicy,
		Blocked
	}

	public sealed class ImportPlanItem
	{
		public LoadedPackage Package;
		public UnityPackageEntry Entry;
		public string IncomingPath;
		public string TargetPath;
		public ImportPlanAction Action;
		public ImportConflict Conflict;
	}

	public sealed class ImportPlan
	{
		public readonly List<ImportPlanItem> OrderedItems = new List<ImportPlanItem>();
		public readonly List<string> Errors = new List<string>();
		public bool CanApply => Errors.Count == 0;
		public int WriteCount
		{
			get
			{
				int count = 0;
				for (int i = 0; i < OrderedItems.Count; i++)
				{
					ImportPlanAction action = OrderedItems[i].Action;
					if (action == ImportPlanAction.WriteNew || action == ImportPlanAction.Overwrite)
						count++;
				}
				return count;
			}
		}
	}

	public static class ImportPlanBuilder
	{
		private sealed class VirtualAsset
		{
			public string Guid;
			public string Path;
			public bool IsFolder;
			public bool FromProject;
			public bool ContentKnown;
			public bool HasAsset;
			public bool HasMeta;
			public string AssetHash;
			public string MetaHash;
		}

		public static ImportPlan Build(
			IEnumerable<LoadedPackage> packages,
			IAssetProbe project,
			ConflictPolicy policy,
			Func<ImportPlanItem, bool> askOverwrite = null)
		{
			var plan = new ImportPlan();
			if (packages == null || project == null)
				return plan;

			if (!Enum.IsDefined(typeof(ConflictPolicy), policy))
				policy = ConflictPolicy.Ask;

			var orderedPackages = new List<LoadedPackage>();
			foreach (LoadedPackage package in packages)
				if (package != null && package.Tree != null)
					orderedPackages.Add(package);
			orderedPackages.Sort((a, b) => a.DropOrder.CompareTo(b.DropOrder));

			var byGuid = new Dictionary<string, VirtualAsset>(StringComparer.OrdinalIgnoreCase);
			var byPath = new Dictionary<string, VirtualAsset>(StringComparer.OrdinalIgnoreCase);
			var removedProjectGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach (LoadedPackage package in orderedPackages)
			{
				HashSet<string> selectedLeaves = GetSelectedLeafPaths(package.Tree);
				var entries = new List<UnityPackageEntry>(package.Entries ?? new List<UnityPackageEntry>());
				entries.Sort((a, b) => a.EntryOrder.CompareTo(b.EntryOrder));

				foreach (UnityPackageEntry entry in entries)
				{
					if (entry == null || !IsSelected(entry, selectedLeaves))
						continue;
					ResolveEntry(plan, package, entry, project, policy, askOverwrite,
						byGuid, byPath, removedProjectGuids);
				}
			}

			return plan;
		}

		private static void ResolveEntry(
			ImportPlan plan,
			LoadedPackage package,
			UnityPackageEntry entry,
			IAssetProbe project,
			ConflictPolicy policy,
			Func<ImportPlanItem, bool> askOverwrite,
			Dictionary<string, VirtualAsset> byGuid,
			Dictionary<string, VirtualAsset> byPath,
			HashSet<string> removedProjectGuids)
		{
			var item = new ImportPlanItem
			{
				Package = package,
				Entry = entry,
				IncomingPath = entry.AssetPath,
				TargetPath = entry.AssetPath
			};
			plan.OrderedItems.Add(item);

			if (!ProjectPackagePath.TryNormalize(entry.AssetPath, out string incomingPath, out string pathError))
			{
				Block(plan, item, pathError);
				return;
			}
			item.IncomingPath = incomingPath;
			item.TargetPath = incomingPath;

			string identityGuid = entry.IdentityGuid;
			VirtualAsset guidHolder = FindByGuid(identityGuid, project, byGuid, byPath, removedProjectGuids);
			if (guidHolder != null)
			{
				item.TargetPath = guidHolder.Path;
				if (!ProjectPackagePath.TryNormalize(item.TargetPath, out string normalizedTarget, out string targetError))
				{
					Block(plan, item, "GUID target is not a writable project path: " + targetError);
					return;
				}
				item.TargetPath = normalizedTarget;
				if (guidHolder.FromProject && !project.ExistsAt(normalizedTarget))
				{
					Block(plan, item, "The existing GUID target is not writable inside this project (it may come from PackageCache): " + normalizedTarget);
					return;
				}
				if (guidHolder.IsFolder != entry.IsFolder)
				{
					Block(plan, item, "The GUID target changes between a file and a folder: " + normalizedTarget);
					return;
				}
				if (!TryValidateTargetShape(normalizedTarget, entry.IsFolder, project, byPath, out string shapeError))
				{
					Block(plan, item, shapeError);
					return;
				}
				if (ContentMatches(guidHolder, entry))
				{
					item.Action = ImportPlanAction.SkipIdentical;
					item.Conflict = new ImportConflict
					{
						Kind = ImportConflictKind.Duplicate,
						ExistingPath = normalizedTarget,
						IncomingPath = incomingPath,
						TargetPath = normalizedTarget,
						ExistingGuid = identityGuid,
						ExistingFromProject = guidHolder.FromProject
					};
					return;
				}

				ImportConflictKind kind = string.Equals(incomingPath, normalizedTarget, StringComparison.OrdinalIgnoreCase)
					? ImportConflictKind.Update
					: ImportConflictKind.GuidConflict;
				item.Conflict = new ImportConflict
				{
					Kind = kind,
					ExistingPath = normalizedTarget,
					IncomingPath = incomingPath,
					TargetPath = normalizedTarget,
					ExistingGuid = identityGuid,
					RequiresDecision = policy == ConflictPolicy.Ask
				};

				if (!ShouldOverwrite(item, policy, askOverwrite))
				{
					item.Action = ImportPlanAction.SkipByPolicy;
					return;
				}

				item.Action = ImportPlanAction.Overwrite;
				guidHolder.Guid = identityGuid;
				guidHolder.IsFolder = entry.IsFolder;
				guidHolder.FromProject = false;
				SetContent(guidHolder, entry);
				byGuid[identityGuid] = guidHolder;
				byPath[normalizedTarget] = guidHolder;
				return;
			}

			VirtualAsset pathHolder = FindByPath(incomingPath, project, byGuid, byPath, removedProjectGuids);
			if (pathHolder == null)
			{
				if (!TryValidateTargetShape(incomingPath, entry.IsFolder, project, byPath, out string shapeError))
				{
					Block(plan, item, shapeError);
					return;
				}
				item.Action = ImportPlanAction.WriteNew;
				item.Conflict = new ImportConflict
				{
					Kind = ImportConflictKind.New,
					IncomingPath = incomingPath,
					TargetPath = incomingPath
				};
				Reserve(identityGuid, incomingPath, entry, byGuid, byPath);
				return;
			}

			if (pathHolder.IsFolder != entry.IsFolder)
			{
				Block(plan, item, "Import would replace a file with a folder or a folder with a file: " + incomingPath);
				return;
			}
			if (!TryValidateTargetShape(incomingPath, entry.IsFolder, project, byPath, out string occupiedShapeError))
			{
				Block(plan, item, occupiedShapeError);
				return;
			}
			if (ContentMatches(pathHolder, entry))
			{
				item.Action = ImportPlanAction.SkipIdentical;
				item.Conflict = new ImportConflict
				{
					Kind = ImportConflictKind.Duplicate,
					ExistingPath = pathHolder.Path,
					IncomingPath = incomingPath,
					TargetPath = incomingPath,
					ExistingGuid = pathHolder.Guid,
					ExistingFromProject = pathHolder.FromProject
				};
				return;
			}

			bool sameIdentity = !string.IsNullOrEmpty(identityGuid)
				&& string.Equals(identityGuid, pathHolder.Guid, StringComparison.OrdinalIgnoreCase);
			ImportConflictKind pathKind = sameIdentity || (entry.IsPathManaged && string.IsNullOrEmpty(pathHolder.Guid))
				? ImportConflictKind.Update
				: ImportConflictKind.PathConflict;
			item.Conflict = new ImportConflict
			{
				Kind = pathKind,
				ExistingPath = pathHolder.Path,
				IncomingPath = incomingPath,
				TargetPath = incomingPath,
				ExistingGuid = pathHolder.Guid,
				RequiresDecision = policy == ConflictPolicy.Ask
			};

			if (!ShouldOverwrite(item, policy, askOverwrite))
			{
				item.Action = ImportPlanAction.SkipByPolicy;
				return;
			}

			item.Action = ImportPlanAction.Overwrite;
			if (!string.IsNullOrEmpty(pathHolder.Guid))
			{
				byGuid.Remove(pathHolder.Guid);
				removedProjectGuids.Add(pathHolder.Guid);
			}
			Reserve(identityGuid, incomingPath, entry, byGuid, byPath);
		}

		private static bool ShouldOverwrite(ImportPlanItem item, ConflictPolicy policy, Func<ImportPlanItem, bool> askOverwrite)
		{
			if (policy == ConflictPolicy.Overwrite)
				return true;
			if (policy == ConflictPolicy.Skip)
				return false;
			// Preview builds do not show modal dialogs and assume overwrite so later
			// package conflicts can still be displayed. Apply supplies the decision callback.
			return askOverwrite == null || askOverwrite(item);
		}

		private static bool TryValidateTargetShape(
			string path,
			bool targetIsFolder,
			IAssetProbe project,
			Dictionary<string, VirtualAsset> byPath,
			out string error)
		{
			error = null;
			string[] parts = path.Split('/');
			string ancestor = parts[0];
			for (int i = 1; i < parts.Length - 1; i++)
			{
				ancestor += "/" + parts[i];
				if (byPath.TryGetValue(ancestor, out VirtualAsset virtualAncestor))
				{
					if (!virtualAncestor.IsFolder)
					{
						error = "A file already occupies an ancestor of the import target: " + ancestor;
						return false;
					}
				}
				else if (project.ExistsAt(ancestor) && !project.IsFolderAt(ancestor))
				{
					error = "A project file already occupies an ancestor of the import target: " + ancestor;
					return false;
				}
			}

			if (!targetIsFolder)
			{
				string descendantPrefix = path + "/";
				foreach (KeyValuePair<string, VirtualAsset> pair in byPath)
				{
					if (pair.Key.StartsWith(descendantPrefix, StringComparison.OrdinalIgnoreCase))
					{
						error = "The import target is already used as a directory by " + pair.Key;
						return false;
					}
				}
			}

			return true;
		}

		private static void Block(ImportPlan plan, ImportPlanItem item, string message)
		{
			item.Action = ImportPlanAction.Blocked;
			item.Conflict = new ImportConflict
			{
				Kind = ImportConflictKind.PathConflict,
				IncomingPath = item.IncomingPath,
				TargetPath = item.TargetPath,
				Message = message
			};
			plan.Errors.Add(message);
		}

		private static VirtualAsset FindByGuid(
			string guid,
			IAssetProbe project,
			Dictionary<string, VirtualAsset> byGuid,
			Dictionary<string, VirtualAsset> byPath,
			HashSet<string> removedProjectGuids)
		{
			if (string.IsNullOrEmpty(guid))
				return null;
			if (byGuid.TryGetValue(guid, out VirtualAsset known))
				return known;
			// A path replacement can remove a GUID that originally came from the project,
			// then a later selected package can establish that GUID at a new path. Always
			// prefer the evolving session index before consulting the project tombstone.
			if (removedProjectGuids.Contains(guid))
				return null;

			string path = project.PathAt(guid);
			if (string.IsNullOrEmpty(path))
				return null;
			var asset = new VirtualAsset { Guid = guid, Path = path, IsFolder = project.IsFolderAt(path), FromProject = true };
			SetProjectContent(asset, project, path);
			byGuid[guid] = asset;
			if (!byPath.ContainsKey(path))
				byPath[path] = asset;
			return asset;
		}

		private static VirtualAsset FindByPath(
			string path,
			IAssetProbe project,
			Dictionary<string, VirtualAsset> byGuid,
			Dictionary<string, VirtualAsset> byPath,
			HashSet<string> removedProjectGuids)
		{
			if (byPath.TryGetValue(path, out VirtualAsset known))
				return known;

			string guid = project.GuidAt(path);
			if (!string.IsNullOrEmpty(guid) && removedProjectGuids.Contains(guid))
				guid = null;
			if (string.IsNullOrEmpty(guid) && !project.ExistsAt(path))
				return null;

			var asset = new VirtualAsset { Guid = guid, Path = path, IsFolder = project.IsFolderAt(path), FromProject = true };
			SetProjectContent(asset, project, path);
			byPath[path] = asset;
			if (!string.IsNullOrEmpty(guid))
				byGuid[guid] = asset;
			return asset;
		}

		private static void Reserve(
			string guid,
			string path,
			UnityPackageEntry entry,
			Dictionary<string, VirtualAsset> byGuid,
			Dictionary<string, VirtualAsset> byPath)
		{
			var asset = new VirtualAsset { Guid = guid, Path = path, IsFolder = entry.IsFolder };
			SetContent(asset, entry);
			byPath[path] = asset;
			if (!string.IsNullOrEmpty(guid))
				byGuid[guid] = asset;
		}

		private static bool ContentMatches(VirtualAsset asset, UnityPackageEntry entry)
		{
			return asset != null
				&& asset.ContentKnown
				&& asset.IsFolder == entry.IsFolder
				&& asset.HasAsset == entry.HasAssetMember
				&& asset.HasMeta == entry.HasMetaMember
				&& string.Equals(asset.AssetHash, entry.AssetHash, StringComparison.Ordinal)
				&& string.Equals(asset.MetaHash, entry.MetaHash, StringComparison.Ordinal);
		}

		private static void SetContent(VirtualAsset asset, UnityPackageEntry entry)
		{
			asset.HasAsset = entry.HasAssetMember;
			asset.HasMeta = entry.HasMetaMember;
			asset.AssetHash = entry.AssetHash;
			asset.MetaHash = entry.MetaHash;
			asset.ContentKnown = (!asset.HasAsset || !string.IsNullOrEmpty(asset.AssetHash))
				&& (!asset.HasMeta || !string.IsNullOrEmpty(asset.MetaHash));
		}

		private static void SetProjectContent(VirtualAsset asset, IAssetProbe project, string path)
		{
			if (asset == null || project == null || !project.TryGetContent(path, out AssetContentFingerprint content))
				return;
			asset.HasAsset = content.HasAsset;
			asset.HasMeta = content.HasMeta;
			asset.AssetHash = content.AssetHash;
			asset.MetaHash = content.MetaHash;
			asset.ContentKnown = (!asset.HasAsset || !string.IsNullOrEmpty(asset.AssetHash))
				&& (!asset.HasMeta || !string.IsNullOrEmpty(asset.MetaHash));
		}

		private static HashSet<string> GetSelectedLeafPaths(PackageAssetNode tree)
		{
			var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (tree == null)
				return selected;
			foreach (PackageAssetNode leaf in tree.EnumerateCheckedLeaves())
				selected.Add(leaf.FullPath);
			return selected;
		}

		private static bool IsSelected(UnityPackageEntry entry, HashSet<string> selectedLeaves)
		{
			if (!entry.IsFolder)
				return selectedLeaves.Contains(entry.AssetPath);
			string prefix = entry.AssetPath + "/";
			foreach (string selected in selectedLeaves)
			{
				if (string.Equals(selected, entry.AssetPath, StringComparison.OrdinalIgnoreCase)
					|| selected.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
					return true;
			}
			return false;
		}
	}
}
