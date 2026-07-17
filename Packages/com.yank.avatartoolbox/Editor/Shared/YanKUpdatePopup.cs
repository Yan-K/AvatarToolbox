using UnityEditor;
using UnityEngine;

namespace YanK
{
	/// <summary>
	/// Small styled popup shown when the user clicks the yellow "Update" button in a Yan-K tool
	/// header. Presents the 3 download destinations plus a confirm-gated "skip this update".
	/// </summary>
	public class YanKUpdatePopup : EditorWindow
	{
		private const string BoothUrl  = "https://yan-k.booth.pm/items/8191277";
		private const string VccUrl    = "https://xtlcdn.github.io/vpm/";
		private const string GithubUrl = "https://github.com/Yan-K/AvatarToolbox";

		public static void ShowPopup()
		{
			var w = CreateInstance<YanKUpdatePopup>();
			w.titleContent = new GUIContent(YanKLocalization.L("updateDialogTitle", "Update Available"));
			w.minSize = new Vector2(340, 300);
			w.maxSize = new Vector2(340, 300);
			w.ShowUtility();
		}

		private void OnGUI()
		{
			GUILayout.Space(10);

			var titleStyle = new GUIStyle(EditorStyles.boldLabel)
			{
				fontSize  = 15,
				alignment = TextAnchor.MiddleCenter
			};
			GUILayout.Label(YanKLocalization.L("updateDialogTitle", "Update Available"), titleStyle, GUILayout.ExpandWidth(true));

			var versionStyle = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter };
			string cur    = YanKUpdateChecker.CurrentVersion ?? "?";
			string latest = YanKUpdateChecker.LatestVersion  ?? "?";
			GUILayout.Label($"v{cur}  →  v{latest}", versionStyle, GUILayout.ExpandWidth(true));

			GUILayout.Space(14);
			var wrapCenter = new GUIStyle(EditorStyles.wordWrappedLabel) { alignment = TextAnchor.MiddleCenter };
			GUILayout.Label(YanKLocalization.L("updateDownloadPrompt", "Choose where to download the update:"), wrapCenter, GUILayout.ExpandWidth(true));
			GUILayout.Space(8);

			DrawLinkCard(YanKLocalization.L("updateSourceBooth", "Booth"), new Color(1f, 0.6f, 0.15f), BoothUrl);
			GUILayout.Space(6);
			DrawLinkCard(YanKLocalization.L("updateSourceVcc", "VCC / ALCOM"), new Color(0.25f, 0.55f, 0.95f), VccUrl);
			GUILayout.Space(6);
			DrawLinkCard(YanKLocalization.L("updateSourceGitHub", "GitHub"), new Color(0.35f, 0.35f, 0.35f), GithubUrl);

			GUILayout.FlexibleSpace();

			if (GUILayout.Button(YanKLocalization.L("updateSkip", "Skip this update"), EditorStyles.linkLabel))
			{
				bool confirm = EditorUtility.DisplayDialog(
					YanKLocalization.L("updateSkipConfirmTitle", "Skip Update"),
					YanKLocalization.L("updateSkipConfirmMessage",
						"This will hide the update notice until the next release. Skip this update?"),
					YanKLocalization.L("updateSkipConfirmYes", "Yes, Skip"),
					YanKLocalization.L("updateSkipConfirmNo", "No, Keep Reminding"));

				if (confirm)
				{
					YanKUpdateChecker.SkipCurrent();
					Close();
				}
			}

			GUILayout.Space(10);
		}

		private void DrawLinkCard(string label, Color color, string url)
		{
			var prevColor = GUI.backgroundColor;
			GUI.backgroundColor = color;
			if (GUILayout.Button(label, GUILayout.Height(32)))
				Application.OpenURL(url);
			GUI.backgroundColor = prevColor;
		}
	}
}
