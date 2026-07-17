using System;
using System.Collections.Generic;
using System.IO;

namespace YanK
{
	public class PackageAssetNode
	{
		public enum ToggleState { Checked, Unchecked, Mixed }

		public string Name;
		public string FullPath;
		public bool IsFolder;
		public List<PackageAssetNode> Children = new List<PackageAssetNode>();
		public PackageAssetNode Parent;
		public bool IsChecked = true;
		public bool IsExpanded = true;
		public long FileSize;
		public string Extension;
		// Importer nodes can be both structural folders and actual package records.
		// This keeps a non-empty folder's .meta associated with the visible folder node.
		public bool HasPackageEntry;

		// Importer-only aggregate flags: true when this node (or, for folders, any
		// checked descendant) currently has a checked GUID / path conflict. Updated
		// by the importer when the checked state changes.
		public bool HasCheckedGuidConflict;
		public bool HasCheckedPathConflict;

		// True when this node (or any checked descendant, for folders) is an Update:
		// the incoming asset overwrites an existing asset at the same path/GUID.
		public bool HasCheckedUpdate;
		public bool HasCheckedDuplicate;

		// Sorts children (folders first, then by name, case-insensitive) recursively.
		// Called once after the tree is built so per-frame drawing never re-sorts.
		public void SortChildrenRecursive()
		{
			if (Children.Count > 1)
			{
				Children.Sort((a, b) =>
				{
					int fc = (b.IsFolder ? 1 : 0) - (a.IsFolder ? 1 : 0);
					if (fc != 0) return fc;
					return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
				});
			}
			for (int i = 0; i < Children.Count; i++)
			{
				if (Children[i].IsFolder)
					Children[i].SortChildrenRecursive();
			}
		}

		public ToggleState GetState()
		{
			if (!IsFolder || Children.Count == 0)
				return IsChecked ? ToggleState.Checked : ToggleState.Unchecked;

			bool anyChecked = false;
			bool anyUnchecked = false;

			foreach (PackageAssetNode c in Children)
			{
				ToggleState s = c.GetState();
				if (s == ToggleState.Mixed)
					return ToggleState.Mixed;
				if (s == ToggleState.Checked)
					anyChecked = true;
				else
					anyUnchecked = true;

				if (anyChecked && anyUnchecked)
					return ToggleState.Mixed;
			}

			return anyChecked ? ToggleState.Checked : ToggleState.Unchecked;
		}

		public void SetChecked(bool value, bool propagateToChildren = true)
		{
			IsChecked = value;

			if (propagateToChildren)
			{
				foreach (PackageAssetNode c in Children)
					c.SetChecked(value, true);
			}

			if (Parent != null)
				Parent.RecomputeFromChildren();
		}

		public void RecomputeFromChildren()
		{
			if (Children.Count == 0)
			{
				if (Parent != null)
					Parent.RecomputeFromChildren();
				return;
			}

			bool anyChecked = false;
			bool anyUnchecked = false;

			foreach (PackageAssetNode c in Children)
			{
				ToggleState s = c.GetState();
				if (s == ToggleState.Mixed)
				{
					anyChecked = true;
					anyUnchecked = true;
					break;
				}
				if (s == ToggleState.Checked)
					anyChecked = true;
				else
					anyUnchecked = true;
			}

			if (anyChecked && !anyUnchecked)
				IsChecked = true;
			else if (!anyChecked && anyUnchecked)
				IsChecked = false;
			else
				IsChecked = false;

			if (Parent != null)
				Parent.RecomputeFromChildren();
		}

		public IEnumerable<PackageAssetNode> EnumerateCheckedLeaves()
		{
			if (!IsFolder || (IsFolder && Children.Count == 0 && HasPackageEntry))
			{
				if (IsChecked)
					yield return this;
				yield break;
			}

			foreach (PackageAssetNode c in Children)
			{
				foreach (PackageAssetNode leaf in c.EnumerateCheckedLeaves())
					yield return leaf;
			}
		}

		// Counts importable leaf items (files, plus any empty-folder entry that never
		// gained children). Folders that contain children are structure, not leaves.
		public int CountLeaves()
		{
			if (!IsFolder || (IsFolder && Children.Count == 0 && HasPackageEntry))
				return 1;

			int total = 0;
			for (int i = 0; i < Children.Count; i++)
				total += Children[i].CountLeaves();
			return total;
		}

		public long ComputeSize()
		{
			if (IsFolder)
			{
				long total = 0;
				foreach (PackageAssetNode c in Children)
					total += c.ComputeSize();
				FileSize = total;
				return total;
			}

			if (FileSize > 0)
				return FileSize;

			string abs = Path.GetFullPath(FullPath);
			if (File.Exists(abs))
			{
				FileSize = new FileInfo(abs).Length;
				return FileSize;
			}

			FileSize = 0;
			return 0;
		}

		public static PackageAssetNode BuildTree(IEnumerable<string> assetPaths)
		{
			PackageAssetNode root = new PackageAssetNode
			{
				Name = "Assets",
				FullPath = "Assets",
				IsFolder = true
			};

			Dictionary<string, PackageAssetNode> index = new Dictionary<string, PackageAssetNode>();
			index["Assets"] = root;

			foreach (string raw in assetPaths)
			{
				if (string.IsNullOrEmpty(raw))
					continue;

				string path = raw.Replace('\\', '/');
				string[] parts = path.Split('/');
				if (parts.Length == 0 || parts[0] != "Assets")
					continue;

				PackageAssetNode parent = root;
				string accum = "Assets";

				for (int i = 1; i < parts.Length; i++)
				{
					string seg = parts[i];
					if (string.IsNullOrEmpty(seg))
						continue;

					accum = accum + "/" + seg;
					bool isLeaf = (i == parts.Length - 1);

					if (!index.TryGetValue(accum, out PackageAssetNode node))
					{
						node = new PackageAssetNode
						{
							Name = seg,
							FullPath = accum,
							IsFolder = !isLeaf,
							Parent = parent
						};
						if (isLeaf)
						{
							int dot = seg.LastIndexOf('.');
							node.Extension = dot >= 0 ? seg.Substring(dot).ToLowerInvariant() : string.Empty;
						}
						parent.Children.Add(node);
						index[accum] = node;
					}
					else if (!isLeaf && !node.IsFolder)
					{
						// A previous entry created this node as a leaf (a .unitypackage can list a
						// folder's own asset entry before any file inside it). Now that a deeper
						// path travels through it, it must be a folder — promote it so its children
						// are not hidden from the tree or skipped during import.
						node.IsFolder = true;
						node.Extension = null;
					}

					parent = node;
				}
			}

			return root;
		}

		public static PackageAssetNode BuildProjectTree(IEnumerable<UnityPackageEntry> entries)
		{
			PackageAssetNode root = new PackageAssetNode
			{
				Name = "Project",
				FullPath = string.Empty,
				IsFolder = true
			};

			Dictionary<string, PackageAssetNode> index =
				new Dictionary<string, PackageAssetNode>(StringComparer.OrdinalIgnoreCase);

			if (entries == null)
				return root;

			foreach (UnityPackageEntry entry in entries)
			{
				if (entry == null || string.IsNullOrEmpty(entry.AssetPath))
					continue;
				if (!ProjectPackagePath.TryNormalize(entry.AssetPath, out string path, out string error))
					throw new InvalidDataException(error);

				string[] parts = path.Split('/');
				PackageAssetNode parent = root;
				string accum = string.Empty;
				for (int i = 0; i < parts.Length; i++)
				{
					string segment = parts[i];
					accum = string.IsNullOrEmpty(accum) ? segment : accum + "/" + segment;
					bool isEntryNode = i == parts.Length - 1;
					bool shouldBeFolder = !isEntryNode || entry.IsFolder;

					if (!index.TryGetValue(accum, out PackageAssetNode node))
					{
						node = new PackageAssetNode
						{
							Name = segment,
							FullPath = accum,
							IsFolder = shouldBeFolder,
							Parent = parent
						};
						if (!shouldBeFolder)
						{
							int dot = segment.LastIndexOf('.');
							node.Extension = dot >= 0 ? segment.Substring(dot).ToLowerInvariant() : string.Empty;
						}
						parent.Children.Add(node);
						index[accum] = node;
					}
					else
					{
						if (!string.Equals(node.FullPath, accum, StringComparison.Ordinal))
							throw new InvalidDataException("Case-colliding package paths: " + node.FullPath + " and " + accum);
						if (!isEntryNode && !node.IsFolder)
							throw new InvalidDataException("A package file is also used as a directory: " + accum);
						if (isEntryNode && node.HasPackageEntry)
							throw new InvalidDataException("Duplicate package pathname: " + accum);
						if (isEntryNode && !entry.IsFolder && node.Children.Count > 0)
							throw new InvalidDataException("A package pathname is both a file and a directory: " + accum);
						if (shouldBeFolder)
						{
							node.IsFolder = true;
							node.Extension = null;
						}
					}

					if (isEntryNode)
						node.HasPackageEntry = true;
					parent = node;
				}
			}

			return root;
		}
	}
}
