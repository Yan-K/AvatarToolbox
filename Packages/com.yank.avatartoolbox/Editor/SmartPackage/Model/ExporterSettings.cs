using System;
using System.Collections.Generic;
using UnityEditor;

namespace YanK
{
	public enum FolderCollectionTopMode { None, NonDestructive, Destructive }

	public class ExporterSettings
	{
		public enum SortMode { Name, Size, Type }

		public string ExcludeExtensionsRaw = ".cs,.shader";
		public string ExcludeNamesRaw = "";
		public SortMode Sort = SortMode.Name;
		public bool Ascending = true;
		public string SearchText = "";
		public FolderCollectionMode CollectionMode = FolderCollectionMode.KeepStructure;
		public FolderCollectionTopMode TopMode = FolderCollectionTopMode.None;

		// When true (default) the exporter automatically collects every asset referenced
		// by the selection. When false, only the assets the user added (folders expand to
		// the files they directly contain) are exported.
		public bool IncludeDependencies = true;

		private const string KeyExcludeExtensions = "YSP_ExcludeExtensions";
		private const string KeyExcludeNames = "YSP_ExcludeGlobs";
		private const string KeySortMode = "YSP_SortMode";
		private const string KeySortAscending = "YSP_SortAscending";
		private const string KeyFolderCollectionMode = "YSP_FolderCollectionMode";
		private const string KeyTopMode = "YSP_FolderCollectionTopMode";
		private const string KeyIncludeDependencies = "YSP_IncludeDependencies";

		public void Load()
		{
			ExcludeExtensionsRaw = EditorPrefs.GetString(KeyExcludeExtensions, ".cs,.shader");
			ExcludeNamesRaw = EditorPrefs.GetString(KeyExcludeNames, "");
			Sort = (SortMode)EditorPrefs.GetInt(KeySortMode, (int)SortMode.Name);
			Ascending = EditorPrefs.GetBool(KeySortAscending, true);
			CollectionMode = (FolderCollectionMode)EditorPrefs.GetInt(KeyFolderCollectionMode, (int)FolderCollectionMode.KeepStructure);
			TopMode = (FolderCollectionTopMode)EditorPrefs.GetInt(KeyTopMode, (int)FolderCollectionTopMode.None);
			IncludeDependencies = EditorPrefs.GetBool(KeyIncludeDependencies, true);
		}

		public void Save()
		{
			EditorPrefs.SetString(KeyExcludeExtensions, ExcludeExtensionsRaw ?? "");
			EditorPrefs.SetString(KeyExcludeNames, ExcludeNamesRaw ?? "");
			EditorPrefs.SetInt(KeySortMode, (int)Sort);
			EditorPrefs.SetBool(KeySortAscending, Ascending);
			EditorPrefs.SetInt(KeyFolderCollectionMode, (int)CollectionMode);
			EditorPrefs.SetInt(KeyTopMode, (int)TopMode);
			EditorPrefs.SetBool(KeyIncludeDependencies, IncludeDependencies);
		}

		public IEnumerable<string> ParseExtensions()
		{
			if (string.IsNullOrEmpty(ExcludeExtensionsRaw))
				yield break;

			string[] parts = ExcludeExtensionsRaw.Split(',');
			foreach (string raw in parts)
			{
				if (string.IsNullOrWhiteSpace(raw))
					continue;

				string s = raw.Trim().ToLowerInvariant();
				if (!s.StartsWith("."))
					s = "." + s;
				yield return s;
			}
		}

		public IEnumerable<string> ParseRegexPatterns()
		{
			if (string.IsNullOrEmpty(ExcludeNamesRaw))
				yield break;

			string[] parts = ExcludeNamesRaw.Split(new[] { ',', '\n', '\r' });
			foreach (string raw in parts)
			{
				if (string.IsNullOrWhiteSpace(raw))
					continue;
				yield return raw.Trim();
			}
		}
	}
}
