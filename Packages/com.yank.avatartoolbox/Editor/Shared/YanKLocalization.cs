using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace YanK
{
	/// <summary>
	/// Shared localization helper usable from both EditorWindow and CustomEditor code.
	/// Uses the same Resources/YATLocalization JSON files and the same EditorPrefs key
	/// (`YAT_Language`) as <see cref="YanKEditorWindow"/>, so the language selection
	/// stays in sync across all Yan-K Avatar Toolbox tools.
	/// </summary>
	public static class YanKLocalization
	{
		private const string LocalizationFolder = "YATLocalization";
		private const string LanguageCatalogChunk = "Shared";
		private static readonly string[] LocalizationChunks =
		{
			LanguageCatalogChunk,
			"MaterialEditor",
			"BlendshapeEditor",
			"SceneController",
			"SmartPackage",
			"NonToonConverter"
		};

		internal const string LanguagePrefKey = "YAT_Language";

		private static readonly List<string> _languages = new List<string>();
		private static Dictionary<string, string> _strings = new Dictionary<string, string>();
		private static int _selectedIndex = -1;
		private static bool _initialized;

		public static IReadOnlyList<string> Languages
		{
			get
			{
				EnsureInitialized();
				return _languages;
			}
		}

		public static int SelectedIndex
		{
			get
			{
				EnsureInitialized();
				return _selectedIndex;
			}
			set
			{
				EnsureInitialized();
				if (value < 0 || value >= _languages.Count) return;
				if (value == _selectedIndex) return;
				_selectedIndex = value;
				EditorPrefs.SetInt(LanguagePrefKey, _selectedIndex);
				LoadStrings();
			}
		}

		public static string CurrentLanguage
		{
			get
			{
				EnsureInitialized();
				return (_selectedIndex >= 0 && _selectedIndex < _languages.Count) ? _languages[_selectedIndex] : "English";
			}
		}

		public static string L(string key, string defaultValue)
		{
			EnsureInitialized();
			return _strings.TryGetValue(key, out var v) ? v : defaultValue;
		}

		public static void Reload()
		{
			_initialized = false;
			EnsureInitialized();
		}

		private static void EnsureInitialized()
		{
			if (_initialized) return;
			_initialized = true;
			RefreshLanguageFiles();

			int saved = EditorPrefs.GetInt(LanguagePrefKey, _languages.IndexOf("English"));
			if (saved < 0 || saved >= _languages.Count) saved = 0;
			_selectedIndex = saved;

			LoadStrings();
		}

		private static void RefreshLanguageFiles()
		{
			_languages.Clear();
			foreach (var file in Resources.LoadAll<TextAsset>($"{LocalizationFolder}/{LanguageCatalogChunk}"))
			{
				string language = Path.GetFileNameWithoutExtension(file.name);
				if (!_languages.Contains(language)) _languages.Add(language);
			}

			if (!_languages.Contains("English"))
				_languages.Insert(0, "English");
		}

		private static void LoadStrings()
		{
			_strings = new Dictionary<string, string>();
			if (_languages.Count == 0) return;

			string lang = _languages[_selectedIndex];
			// English is the canonical fallback for any key omitted by another language.
			MergeLanguage("English", _strings, lang == "English");
			if (lang != "English") MergeLanguage(lang, _strings, true);
		}

		private static void MergeLanguage(string language, Dictionary<string, string> destination, bool reportMissingChunks)
		{
			var languageKeys = new HashSet<string>();

			foreach (string chunk in LocalizationChunks)
			{
				var jsonFile = Resources.Load<TextAsset>($"{LocalizationFolder}/{chunk}/{language}");
				if (jsonFile == null)
				{
					if (reportMissingChunks)
						Debug.LogWarning($"Localization chunk not found: {chunk}/{language}");
					continue;
				}

				try
				{
					foreach (var entry in LocalizationParser.Parse(jsonFile.text))
					{
						if (!languageKeys.Add(entry.Key))
							Debug.LogError($"Duplicate localization key '{entry.Key}' in language '{language}'.");
						destination[entry.Key] = entry.Value;
					}
				}
				catch (Exception exception)
				{
					Debug.LogError($"Failed to parse localization chunk {chunk}/{language}: {exception.Message}");
				}
			}
		}
	}
}
