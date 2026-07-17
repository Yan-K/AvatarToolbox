using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;

namespace YanK
{
	public partial class BlendshapeEditorTool
	{
		// Drawn as content inside DrawAnimationTab (no foldout, no outer GroupBox).
		private void DrawExportContent()
		{
			// Exporting while previewing would capture preview values; lock it.
			GUI.enabled = !inPreview;

			int count = CountForExport(exportMode);
			GUI.enabled = !inPreview && count > 0 && targetRenderer != null;
			if (GUILayout.Button(L("bseExportButton", "Export..."), GUILayout.Height(24)))
				ExportAnimationClip();
			GUI.enabled = !inPreview;

			GUILayout.Space(2);

			string[] modeNames =
			{
				L("bseExportAll", "All Blendshapes"),
				L("bseExportNonZero", "Non-Zero Only"),
				L("bseExportModified", "Modified Only"),
				L("bseCustomMode", "Custom"),
			};
			int current = (int)exportMode;
			int newMode = GUILayout.Toolbar(current, modeNames, GUILayout.Height(22));
			if (newMode != current) exportMode = (ExportMode)newMode;

			EditorGUILayout.LabelField(string.Format(L("bseExportCount", "Blendshapes to export: {0}"), count), dimLabelStyle);

			GUI.enabled = true;
		}

		private int CountForExport(ExportMode mode)
		{
			int c = 0;
			foreach (var s in slots)
			{
				if (s.isSeparator) continue;
				if (IncludeInExport(s, mode)) c++;
			}
			return c;
		}

		private bool IncludeInExport(BlendshapeSlot s, ExportMode mode)
		{
			switch (mode)
			{
				case ExportMode.AllBlendshapes: return true;
				case ExportMode.NonZero: return Mathf.Abs(s.current) > 0.001f;
				case ExportMode.Modified: return Mathf.Abs(s.current - s.defaultValue) > 0.01f;
				case ExportMode.CustomSelected: return s.selected;
			}
			return false;
		}

		private void ExportAnimationClip()
		{
			if (targetRenderer == null) return;

			string defaultName = targetRenderer.name + "_blendshapes.anim";
			string path = EditorUtility.SaveFilePanelInProject(
				L("bseExportSaveTitle", "Save Blendshape Animation"),
				defaultName,
				"anim",
				L("bseExportSaveMsg", "Select a location to save the animation."));

			if (string.IsNullOrEmpty(path)) return;

			string relativePath = GetRendererAnimationPath();

			var clip = new AnimationClip();
			clip.name = Path.GetFileNameWithoutExtension(path);

			int written = 0;
			foreach (var s in slots)
			{
				if (s.isSeparator) continue;
				if (!IncludeInExport(s, exportMode)) continue;

				var curve = AnimationCurve.Constant(0f, 1f / 60f, s.current);
				clip.SetCurve(relativePath, typeof(SkinnedMeshRenderer), "blendShape." + s.name, curve);
				written++;
			}

			if (written == 0)
			{
				EditorUtility.DisplayDialog(
					L("bseExportTitle", "Export Animation"),
					L("bseExportNothing", "No blendshapes match the current export mode."),
					"OK");
				return;
			}

			AssetDatabase.CreateAsset(clip, path);
			AssetDatabase.SaveAssets();
			var saved = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
			EditorGUIUtility.PingObject(saved);
		}

		/// <summary>
		/// Returns the animator-relative path (parent-to-child slashed path) for the SkinnedMeshRenderer.
		/// Falls back to just the SMR name if no avatarRoot is assigned.
		/// </summary>
		private string GetRendererAnimationPath()
		{
			if (targetRenderer == null) return "";
			if (avatarRoot == null) return targetRenderer.name;
			return AnimationUtility.CalculateTransformPath(targetRenderer.transform, avatarRoot.transform);
		}
	}
}
