using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace YanK
{
	// Opens only when something failed: each failed file and why, with the counts for the rest.
	public sealed class SmartPackageResultWindow : EditorWindow
	{
		// Serialized so the list survives the script reload an import can start.
		[SerializeField] private int imported;
		[SerializeField] private int skipped;
		[SerializeField] private List<string> failedPaths = new List<string>();
		[SerializeField] private List<string> failedReasons = new List<string>();
		[SerializeField] private List<string> importedPaths = new List<string>();
		private Vector2 scroll;

		public static void Show(ImportRunResult result)
		{
			var window = CreateInstance<SmartPackageResultWindow>();
			window.imported = result.Written + result.Updated;
			window.skipped = result.Skipped;
			foreach (ImportFailure failure in result.Failures)
			{
				window.failedPaths.Add(failure.Path ?? string.Empty);
				window.failedReasons.Add(failure.Reason ?? string.Empty);
			}
			window.importedPaths.AddRange(result.ImportedPaths);
			window.titleContent = new GUIContent(L("yspResultTitle", "Import Results"));
			window.minSize = new Vector2(440f, 280f);
			Rect main = EditorGUIUtility.GetMainWindowPosition();
			window.position = new Rect(main.x + (main.width - 560f) * 0.5f, main.y + (main.height - 360f) * 0.5f, 560f, 360f);
			window.ShowUtility();
		}

		private static string L(string key, string defaultValue)
		{
			return YanKLocalization.L(key, defaultValue);
		}

		private void OnGUI()
		{
			if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
			{
				Event.current.Use();
				Close();
				GUIUtility.ExitGUI();
			}

			GUILayout.Label(imported > 0 ? L("yspResultPartly", "Partly imported") : L("yspResultFailed", "Import failed"),
				EditorStyles.boldLabel);
			GUILayout.Label(string.Format(L("yspResultCounts", "Imported: {0}  Skipped: {1}  Failed: {2}"),
				imported, skipped, failedPaths.Count));

			scroll = EditorGUILayout.BeginScrollView(scroll);
			for (int i = 0; i < failedPaths.Count; i++)
			{
				EditorGUILayout.SelectableLabel(failedPaths[i], GUILayout.Height(EditorGUIUtility.singleLineHeight));
				if (i < failedReasons.Count && !string.IsNullOrEmpty(failedReasons[i]))
					GUILayout.Label(failedReasons[i], EditorStyles.wordWrappedMiniLabel);
			}
			EditorGUILayout.EndScrollView();

			using (new EditorGUILayout.HorizontalScope())
			{
				GUILayout.FlexibleSpace();
				if (importedPaths.Count > 0 && GUILayout.Button(L("yspShowInProject", "Show in Project")))
					ShowInProject();
				if (GUILayout.Button(L("yspClose", "Close")))
					Close();
			}
		}

		// Highlights the deepest folder holding everything imported, or else each top-level item.
		private void ShowInProject()
		{
			List<string[]> paths = importedPaths.Where(path => !string.IsNullOrEmpty(path))
				.Select(path => path.Split('/')).ToList();
			if (paths.Count == 0)
				return;
			int common = paths[0].Length;
			foreach (string[] path in paths)
			{
				int same = 0;
				while (same < common && same < path.Length && path[same] == paths[0][same])
					same++;
				common = same;
			}
			IEnumerable<string> targets = common >= 2
				? new[] { string.Join("/", paths[0].Take(common)) }
				: paths.Select(path => string.Join("/", path.Take(Math.Min(2, path.Length)))).Distinct();
			UnityEngine.Object[] found = targets.Select(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>)
				.Where(target => target != null).ToArray();
			if (found.Length == 0)
				return;
			Selection.objects = found;
			EditorUtility.FocusProjectWindow();
			EditorGUIUtility.PingObject(found[0]);
		}
	}
}
