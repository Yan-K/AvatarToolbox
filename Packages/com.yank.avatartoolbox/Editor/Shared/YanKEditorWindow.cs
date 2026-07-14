using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.IO;

namespace YanK
{
	public abstract class YanKEditorWindow : EditorWindow
	{
		// --- Abstract / Virtual ---

		protected abstract string ToolTitleKey { get; }
		protected abstract string ToolTitleDefault { get; }

		protected virtual string LocalizationFolder => "YATLocalization";

		// --- Spacing Constants ---

		protected const float ItemSpacing = 4f;
		protected const float GroupPadding = 6f;

		// --- Cached Styles ---

		protected GUIStyle sectionHeaderStyle;
		protected GUIStyle statusLabelStyle;
		protected GUIStyle searchFieldStyle;
		protected GUIStyle dimLabelStyle;
		protected GUIStyle cardStyle;
		protected GUIStyle modifiedCardStyle;
		protected GUIStyle centeredMessageStyle;

		protected bool stylesInitialized;

		// --- Localization ---

		protected readonly List<string> availableLanguages = new List<string>();
		protected int selectedLanguageIndex;
		protected Dictionary<string, string> localizedStrings = new Dictionary<string, string>();

		protected virtual void OnEnable()
		{
			RefreshLanguageFiles();
			LoadDefaultLanguage();
			LoadLocalizedStrings();

			// Re-check for updates every time a tool window is opened/reopened, bypassing the
			// per-OnGUI throttle so the button always reflects the latest release on open.
			YanKUpdateChecker.CheckOnWindowOpen();
		}

		// --- Styles ---

		protected void InitStyles()
		{
			if (stylesInitialized && cardStyle != null) return;

			sectionHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
			{
				fontSize = 13,
				padding = new RectOffset(4, 0, 2, 2)
			};

			statusLabelStyle = new GUIStyle(EditorStyles.label)
			{
				alignment = TextAnchor.MiddleRight,
				normal = { textColor = EditorGUIUtility.isProSkin ? new Color(0.7f, 0.7f, 0.7f) : new Color(0.4f, 0.4f, 0.4f) }
			};

			searchFieldStyle = new GUIStyle(EditorStyles.toolbarSearchField);

			dimLabelStyle = new GUIStyle(EditorStyles.miniLabel)
			{
				alignment = TextAnchor.MiddleRight,
				normal = { textColor = EditorGUIUtility.isProSkin ? new Color(0.55f, 0.55f, 0.55f) : new Color(0.45f, 0.45f, 0.45f) }
			};

			cardStyle = new GUIStyle("box")
			{
				padding = new RectOffset(6, 6, 4, 4),
				margin = new RectOffset(0, 0, 2, 2)
			};

			modifiedCardStyle = new GUIStyle()
			{
				padding = new RectOffset(6, 6, 4, 4),
				margin = new RectOffset(0, 0, 2, 2),
				border = new RectOffset(0, 0, 0, 0)
			};

			var modBgTex = MakeTex(2, 2, EditorGUIUtility.isProSkin ? new Color(0.35f, 0.33f, 0.15f) : new Color(0.95f, 0.90f, 0.65f));
			modifiedCardStyle.normal.background = modBgTex;

			centeredMessageStyle = new GUIStyle(EditorStyles.label)
			{
				alignment = TextAnchor.MiddleCenter,
				wordWrap = true
			};

			stylesInitialized = true;
		}

		// --- Colors ---

		protected static Color SeparatorColor => EditorGUIUtility.isProSkin
			? new Color(0.2f, 0.2f, 0.2f)
			: new Color(0.7f, 0.7f, 0.7f);

		protected static Color HeaderBarColor => EditorGUIUtility.isProSkin
			? new Color(0.35f, 0.55f, 0.9f, 0.8f)
			: new Color(0.3f, 0.5f, 0.85f, 0.8f);

		// --- Drawing Helpers ---

		protected void DrawGroupBox(Action content)
		{
			EditorGUILayout.BeginVertical(EditorStyles.helpBox);
			GUILayout.Space(GroupPadding / 2);
			content?.Invoke();
			GUILayout.Space(GroupPadding / 2);
			EditorGUILayout.EndVertical();
		}

		protected void DrawStyledSeparator()
		{
			GUILayout.Space(4);
			EditorGUI.DrawRect(EditorGUILayout.GetControlRect(false, 1), SeparatorColor);
			GUILayout.Space(4);
		}

		protected string DrawSearchField(string currentFilter)
		{
			EditorGUILayout.BeginHorizontal();
			GUILayout.Space(2);

			var newFilter = EditorGUILayout.TextField(currentFilter, searchFieldStyle);

			if (!string.IsNullOrEmpty(newFilter))
			{
				if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(20)))
					newFilter = "";
			}

			GUILayout.Space(2);
			EditorGUILayout.EndHorizontal();
			return newFilter;
		}

		// Cache 1×1 solid-color textures so we don't leak a new Texture2D every OnGUI / domain reload.
		private static readonly System.Collections.Generic.Dictionary<Color, Texture2D> _texCache
			= new System.Collections.Generic.Dictionary<Color, Texture2D>();

		protected static Texture2D MakeTex(int width, int height, Color color)
		{
			if (_texCache.TryGetValue(color, out var cached) && cached != null) return cached;
			var pixels = new Color[width * height];
			for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
			var tex = new Texture2D(width, height);
			tex.SetPixels(pixels);
			tex.Apply();
			tex.hideFlags = HideFlags.HideAndDontSave;
			_texCache[color] = tex;
			return tex;
		}

		// --- Shared Header ---

		protected virtual void DrawHeader()
		{
			GUILayout.Space(8);

			// Sync language from the shared static class in case YSC (or another tool)
			// changed it while this window was already open.
			int sharedIndex = YanKLocalization.SelectedIndex;
			if (sharedIndex != selectedLanguageIndex)
			{
				selectedLanguageIndex = sharedIndex;
				LoadLocalizedStrings();
			}

			// Check (throttled) for a newer Yan-K Avatar Toolbox release.
			YanKUpdateChecker.EnsureChecked();

			// Row 1: Title + Update button + Language selector + Version badge
			EditorGUILayout.BeginHorizontal();

			EditorGUILayout.LabelField(L(ToolTitleKey, ToolTitleDefault), sectionHeaderStyle, GUILayout.ExpandWidth(false));

			GUILayout.FlexibleSpace();

			if (YanKUpdateChecker.ShouldShowButton())
				YanKUpdateChecker.DrawUpdateButton();

			int newIndex = EditorGUILayout.Popup(selectedLanguageIndex, availableLanguages.ToArray(), GUILayout.Width(120));
			if (newIndex != selectedLanguageIndex)
			{
				selectedLanguageIndex = newIndex;
				// Keep the shared static class (used by YSC) in sync — this also writes EditorPrefs.
				YanKLocalization.SelectedIndex = newIndex;
				LoadLocalizedStrings();
			}

			EditorGUILayout.EndHorizontal();

			GUILayout.Space(8);
		}

		protected void DrawCenteredMessage(string message, string iconName)
		{
			GUILayout.FlexibleSpace();
			EditorGUILayout.BeginHorizontal();
			GUILayout.FlexibleSpace();
			EditorGUILayout.BeginVertical();

			var iconRect = GUILayoutUtility.GetRect(48, 48, GUILayout.ExpandWidth(false));
			GUI.DrawTexture(iconRect, EditorGUIUtility.IconContent(iconName).image, ScaleMode.ScaleToFit);

			GUILayout.Space(8);
			EditorGUILayout.LabelField(message, centeredMessageStyle, GUILayout.Width(300));

			EditorGUILayout.EndVertical();
			GUILayout.FlexibleSpace();
			EditorGUILayout.EndHorizontal();
			GUILayout.FlexibleSpace();
		}

		// --- Localization ---

		private void RefreshLanguageFiles()
		{
			availableLanguages.Clear();

			foreach (var file in Resources.LoadAll<TextAsset>(LocalizationFolder))
				availableLanguages.Add(Path.GetFileNameWithoutExtension(file.name));

			if (!availableLanguages.Contains("English"))
				availableLanguages.Insert(0, "English");
		}

		private void LoadDefaultLanguage()
		{
			selectedLanguageIndex = EditorPrefs.GetInt(YanKLocalization.LanguagePrefKey, availableLanguages.IndexOf("English"));
			if (selectedLanguageIndex < 0 || selectedLanguageIndex >= availableLanguages.Count)
				selectedLanguageIndex = 0;
		}

		private void LoadLocalizedStrings()
		{
			localizedStrings.Clear();
			if (availableLanguages.Count == 0) return;

			string lang = availableLanguages[selectedLanguageIndex];
			var jsonFile = Resources.Load<TextAsset>($"{LocalizationFolder}/{lang}");

			if (jsonFile != null)
			{
				try { localizedStrings = LocalizationParser.Parse(jsonFile.text); }
				catch { Debug.LogError($"Failed to parse localization file: {lang}"); }
			}
			else
			{
				Debug.LogWarning($"Localization file for {lang} not found.");
			}

			Repaint();
		}

		protected string L(string key, string defaultValue)
		{
			return localizedStrings.TryGetValue(key, out string value) ? value : defaultValue;
		}
	}
}
