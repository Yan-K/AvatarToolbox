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
			if (!IsFolder)
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

					parent = node;
				}
			}

			return root;
		}
	}
}
