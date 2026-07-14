using System;
using UnityEditor;
using UnityEngine;

namespace YanK
{
	/// <summary>
	/// Drawing helpers for CustomEditor (Inspector) code that mirror the look-and-feel
	/// used by <see cref="YanKEditorWindow"/>. Styles are lazily built and cached.
	/// </summary>
	public static class YanKInspectorGUI
	{
		public const float ItemSpacing = 4f;
		public const float GroupPadding = 6f;

		private static GUIStyle _sectionHeaderStyle;
		private static GUIStyle _cardStyle;
		private static GUIStyle _centeredMessageStyle;

		public static GUIStyle SectionHeaderStyle
		{
			get
			{
				EnsureStyles();
				return _sectionHeaderStyle;
			}
		}

		public static GUIStyle CardStyle
		{
			get
			{
				EnsureStyles();
				return _cardStyle;
			}
		}

		public static GUIStyle CenteredMessageStyle
		{
			get
			{
				EnsureStyles();
				return _centeredMessageStyle;
			}
		}

		public static Color SeparatorColor => EditorGUIUtility.isProSkin
			? new Color(0.2f, 0.2f, 0.2f)
			: new Color(0.7f, 0.7f, 0.7f);

		public static void EnsureStyles()
		{
			if (_sectionHeaderStyle != null) return;

			_sectionHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
			{
				fontSize = 13,
				padding = new RectOffset(4, 0, 2, 2)
			};

			_cardStyle = new GUIStyle("box")
			{
				padding = new RectOffset(6, 6, 4, 4),
				margin = new RectOffset(0, 0, 2, 2)
			};

			_centeredMessageStyle = new GUIStyle(EditorStyles.label)
			{
				alignment = TextAnchor.MiddleCenter,
				wordWrap = true
			};
		}

		public static void DrawGroupBox(Action content)
		{
			EditorGUILayout.BeginVertical(EditorStyles.helpBox);
			GUILayout.Space(GroupPadding / 2);
			content?.Invoke();
			GUILayout.Space(GroupPadding / 2);
			EditorGUILayout.EndVertical();
		}

		public static void DrawStyledSeparator()
		{
			GUILayout.Space(4);
			EditorGUI.DrawRect(EditorGUILayout.GetControlRect(false, 1), SeparatorColor);
			GUILayout.Space(4);
		}

		public static bool DrawFoldoutHeader(string label, bool expanded)
		{
			return EditorGUILayout.Foldout(expanded, label, true, EditorStyles.foldoutHeader);
		}

		public static void DrawHeaderRow(string title)
		{
			EnsureStyles();
			YanKUpdateChecker.EnsureChecked();
			GUILayout.Space(8);
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField(title, _sectionHeaderStyle, GUILayout.ExpandWidth(false));
			GUILayout.FlexibleSpace();

			if (YanKUpdateChecker.ShouldShowButton())
				YanKUpdateChecker.DrawUpdateButton();

			var langs = YanKLocalization.Languages;
			if (langs.Count > 0)
			{
				int cur = YanKLocalization.SelectedIndex;
				var arr = new string[langs.Count];
				for (int i = 0; i < langs.Count; i++) arr[i] = langs[i];
				int next = EditorGUILayout.Popup(cur, arr, GUILayout.Width(120));
				if (next != cur) YanKLocalization.SelectedIndex = next;
			}

			EditorGUILayout.EndHorizontal();
			GUILayout.Space(8);
		}

		// Cache solid-color textures so we don't leak a new Texture2D on every call.
		private static readonly System.Collections.Generic.Dictionary<Color, Texture2D> _texCache
			= new System.Collections.Generic.Dictionary<Color, Texture2D>();

		public static Texture2D MakeTex(int w, int h, Color color)
		{
			if (_texCache.TryGetValue(color, out var cached) && cached != null) return cached;
			var pixels = new Color[w * h];
			for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
			var tex = new Texture2D(w, h);
			tex.SetPixels(pixels);
			tex.Apply();
			tex.hideFlags = HideFlags.HideAndDontSave;
			_texCache[color] = tex;
			return tex;
		}
	}
}
