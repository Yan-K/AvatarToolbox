using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YanK
{
	internal static class SceneControllerMenu
	{
		private const string MenuPath = "Tools/Yan-K/Scene Controller";
		private const string PostProcessingPackageName = "com.unity.postprocessing";

		[MenuItem(MenuPath)]
		public static void CreateOrSelect()
		{
			var existing = FindExistingController();
			if (existing == null)
			{
				var go = new GameObject(SceneController.RootName);
				Undo.RegisterCreatedObjectUndo(go, "Create Yan-K Scene Controller");
				existing = Undo.AddComponent<SceneController>(go);
				SceneControllerRig.Build(existing);
				ScanAndOfferToDisableExisting(existing);
				OfferToInstallPostProcessingIfMissing();
			}

			SceneControllerEditor.OpenFor(existing);
		}

		public static SceneController FindExistingController()
		{
			for (int i = 0; i < SceneManager.sceneCount; i++)
			{
				var s = SceneManager.GetSceneAt(i);
				if (!s.isLoaded) continue;
				foreach (var root in s.GetRootGameObjects())
				{
					var sc = root.GetComponentInChildren<SceneController>(true);
					if (sc != null) return sc;
				}
			}
			return null;
		}

		// ---------- Scene scan ----------

		/// <summary>
		/// Offers to disable external cameras that may fight with Scene Controller's rig.
		/// Lights are NOT scanned — the new Scene section owns lighting via its enable toggle.
		/// </summary>
		public static void ScanAndOfferToDisableExisting(SceneController sc)
		{
			var cams = new List<Camera>();
			CollectExternalCameras(sc, cams);

			if (cams.Count == 0) return;

			var sb = new StringBuilder();
			sb.AppendLine(YanKLocalization.L("scScanIntro",
				"These cameras are present in the scene and may interfere with Scene Controller. Disable them now?"));
			sb.AppendLine();
			foreach (var c in cams) sb.AppendLine("  • " + c.name);

			if (EditorUtility.DisplayDialog(
				YanKLocalization.L("scScanTitle", "Existing Cameras Detected"),
				sb.ToString(),
				YanKLocalization.L("scDisable", "Disable"),
				YanKLocalization.L("scKeep", "Keep")))
			{
				foreach (var c in cams)
				{
					Undo.RecordObject(c.gameObject, "Disable Existing Camera");
					c.gameObject.SetActive(false);
				}
				EditorSceneManager.MarkAllScenesDirty();
			}
		}

		private static void CollectExternalCameras(SceneController sc, List<Camera> cams)
		{
			var scRoot = sc != null ? sc.transform.root : null;

			foreach (var cam in Object.FindObjectsOfType<Camera>(true))
			{
				if (!cam.gameObject.activeInHierarchy) continue;
				if (IsInsideAvatar(cam.transform)) continue;
				if (scRoot != null && cam.transform.IsChildOf(scRoot)) continue;
				cams.Add(cam);
			}
		}

		/// <summary>
		/// Offers to disable every scene Light that is neither inside an Animator
		/// (avatar) nor a child of the Scene Controller. Used when the user enables
		/// Scene Settings to avoid double-lit scenes.
		/// </summary>
		public static void ScanAndOfferToDisableExternalLights(SceneController sc)
		{
			var lights = new List<Light>();
			var scRoot = sc != null ? sc.transform.root : null;
			foreach (var l in Object.FindObjectsOfType<Light>(true))
			{
				if (l == null) continue;
				if (!l.gameObject.activeInHierarchy) continue;
				if (IsInsideAvatar(l.transform)) continue;
				if (scRoot != null && l.transform.IsChildOf(scRoot)) continue;
				lights.Add(l);
			}

			if (lights.Count == 0) return;

			var sb = new StringBuilder();
			sb.AppendLine(YanKLocalization.L("scScanLightsIntro",
				"These lights are outside all avatars and may overpower Scene Controller's lighting rig. Disable them now?"));
			sb.AppendLine();
			foreach (var l in lights) sb.AppendLine("  • " + l.name);

			if (EditorUtility.DisplayDialog(
				YanKLocalization.L("scScanLightsTitle", "External Lights Detected"),
				sb.ToString(),
				YanKLocalization.L("scDisable", "Disable"),
				YanKLocalization.L("scKeep", "Keep")))
			{
				foreach (var l in lights)
				{
					Undo.RecordObject(l.gameObject, "Disable External Light");
					l.gameObject.SetActive(false);
				}
				EditorSceneManager.MarkAllScenesDirty();
			}
		}

		private static bool IsInsideAvatar(Transform t)
		{
			// Anything that has an Animator in itself or any parent is treated as "inside an avatar".
			return t.GetComponentInParent<Animator>() != null;
		}

		// ---------- Post Processing package install ----------

		private static void OfferToInstallPostProcessingIfMissing()
		{
			if (PostProcessingReflection.IsAvailable) return;

			if (EditorUtility.DisplayDialog(
				YanKLocalization.L("scPpMissingTitle", "Post Processing not installed"),
				YanKLocalization.L("scPpMissingMsg",
					"Post Processing package is required for the Post Processing section. Install com.unity.postprocessing now?"),
				YanKLocalization.L("scInstall", "Install"),
				YanKLocalization.L("scCancel", "Cancel")))
			{
				Client.Add(PostProcessingPackageName);
			}
		}
	}
}
