using UnityEditor;
using UnityEngine;

namespace YanK
{
	/// <summary>
	/// Standalone editor window for the Yan-K Scene Controller.
	/// </summary>
	public partial class SceneControllerEditor : EditorWindow
	{
		[SerializeField] private SceneController sc;
		[SerializeField] private string _scScenePath;   // fallback id used to re-find across play mode
		[SerializeField] private bool _autoRef = true;  // auto-reference avatar when none is assigned

		private bool _avatarFoldout = true;
		private bool _inputFoldout;
		private bool _cameraFoldout = true;
		private bool _sceneFoldout = true;

		private Vector2 _scroll;

		private const string WindowTitle = "Yan-K Scene Controller";

		public static void ShowWindow()
		{
			var w = GetWindow<SceneControllerEditor>(false, WindowTitle, true);
			w.minSize = new Vector2(340f, 420f);
			w.Show();
		}

		public static void OpenFor(SceneController controller)
		{
			var w = GetWindow<SceneControllerEditor>(false, WindowTitle, true);
			w.sc = controller;
			w._scScenePath = PathOf(controller);
			w.minSize = new Vector2(340f, 420f);
			w.Show();
		}

		private static string PathOf(SceneController c)
		{
			if (c == null) return null;
			var t = c.transform;
			string path = t.name;
			while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
			return path;
		}

		private void OnEnable()
		{
			TryRebindController();
			OnEnableCamera();
			OnEnableScene();
			OnEnablePostProcessing();
			EditorApplication.hierarchyChanged -= OnHierarchyChanged;
			EditorApplication.hierarchyChanged += OnHierarchyChanged;
			EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
			EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
		}

		private void OnDisable()
		{
			EditorApplication.hierarchyChanged -= OnHierarchyChanged;
			EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
		}

		// Invalidate cached preset lists whenever the user re-focuses the window so
		// external edits to preset assets / EditorPrefs are picked up without a domain reload.
		private void OnFocus()
		{
			_dirPresetsMerged = null;
			_ppProfiles = null;
			Repaint();
		}

		private void OnHierarchyChanged()
		{
			if (sc == null || !_autoRef) return;
			// Detect avatar becoming inactive (SetActive(false) fires hierarchyChanged).
			if (sc.avatarRoot != null && !sc.avatarRoot.gameObject.activeInHierarchy)
			{
				Undo.RecordObject(sc, "Avatar Deactivated \u2013 Clear Reference");
				sc.avatarRoot = null;
				sc.avatarRootName = null;
				TryRebindAvatar();
				Repaint();
			}
		}

		private void OnPlayModeStateChanged(PlayModeStateChange change)
		{
			// Scene object refs are invalidated by Unity across play-mode transitions.
			// After each transition completes, rebind by path / name so the UI keeps working.
			if (change == PlayModeStateChange.EnteredPlayMode ||
			    change == PlayModeStateChange.EnteredEditMode)
			{
				EditorApplication.delayCall += () =>
				{
					if (this == null) return;
					TryRebindController();
					TryRebindAvatar();
					Repaint();
				};
			}
		}

		private void TryRebindController()
		{
			if (sc != null)
			{
				_scScenePath = PathOf(sc);
				return;
			}
			var found = SceneControllerMenu.FindExistingController();
			if (found != null) { sc = found; _scScenePath = PathOf(found); }
		}

		private void TryRebindAvatar()
		{
			if (sc == null) return;
			if (sc.avatarRoot != null) return;

			// 1) Try to relink by previously-known name (handles other tools that
			// destroy & recreate an avatar with the same name during runtime edits).
			sc.TryRelinkAvatarByName();
			if (sc.avatarRoot != null) return;

			// 2) Auto-detect: only when auto-ref is enabled.
			if (!_autoRef) return;

			var picked = TryFindUniqueSceneAvatar();
			if (picked != null)
			{
				Undo.RecordObject(sc, "Auto-link Avatar");
				sc.avatarRoot = picked;
				sc.avatarRootName = picked.gameObject.name;
				sc.avatarHomePosition = picked.transform.position;
				sc.avatarHomeCaptured = true;
				sc.avatarOffset = Vector3.zero;
				EditorUtility.SetDirty(sc);
			}
		}

		/// <summary>
		/// Returns the single active humanoid Animator in the scene, or null when
		/// there are zero or multiple candidates (we never auto-pick in that case).
		/// </summary>
		private static Animator TryFindUniqueSceneAvatar()
		{
			Animator found = null;
			var all = Object.FindObjectsOfType<Animator>(false);
			foreach (var a in all)
			{
				if (a == null || !a.isActiveAndEnabled) continue;
				if (a.avatar == null || !a.avatar.isHuman) continue;
				// Skip animators inside our own Scene Controller hierarchy.
				if (a.GetComponentInParent<SceneController>() != null) continue;
				if (found != null) return null; // ambiguous — bail out
				found = a;
			}
			return found;
		}

		private void OnSelectionChange()
		{
			if (Selection.activeGameObject == null) return;
			var picked = Selection.activeGameObject.GetComponentInParent<SceneController>();
			if (picked != null && picked != sc)
			{
				sc = picked;
				_scScenePath = PathOf(picked);
				Repaint();
			}
		}

		private void OnInspectorUpdate()
		{
			// Belt-and-braces: poll for lost refs and transparently repair.
			if (sc == null) TryRebindController();
			else if (sc.avatarRoot == null) TryRebindAvatar();
			Repaint();
		}

		private void OnGUI()
		{
			YanKInspectorGUI.EnsureStyles();
			YanKInspectorGUI.DrawHeaderRow(WindowTitle);

			DrawControllerSelector();

			if (sc == null)
			{
				EditorGUILayout.HelpBox(
					YanKLocalization.L("scNoControllerHelp",
						"No Scene Controller found in the loaded scenes.\nClick Create to add one."),
					MessageType.Info);
				if (GUILayout.Button(YanKLocalization.L("scCreate", "Create Scene Controller")))
				{
					SceneControllerMenu.CreateOrSelect();
					sc = SceneControllerMenu.FindExistingController();
					_scScenePath = PathOf(sc);
				}
				return;
			}

			_scroll = EditorGUILayout.BeginScrollView(_scroll);

			DrawAvatarRootRow();

			if (sc.avatarRoot == null)
			{
				EditorGUILayout.HelpBox(
					YanKLocalization.L("scNoAvatarWarning",
						"No avatar assigned. Avatar controls are disabled; Camera and Input are still accessible."),
					MessageType.Info);
			}

			YanKInspectorGUI.DrawGroupBox(() =>
			{
				_avatarFoldout = YanKInspectorGUI.DrawFoldoutHeader(
					YanKLocalization.L("scAvatarSection", "Avatar"), _avatarFoldout);
				if (_avatarFoldout) DrawAvatarSection();
			});

			YanKInspectorGUI.DrawGroupBox(() =>
			{
				_inputFoldout = YanKInspectorGUI.DrawFoldoutHeader(
					YanKLocalization.L("scInputSection", "Play Mode Input"), _inputFoldout);
				if (_inputFoldout) DrawMovementInputSection();
			});

			YanKInspectorGUI.DrawGroupBox(() =>
			{
				_cameraFoldout = YanKInspectorGUI.DrawFoldoutHeader(
					YanKLocalization.L("scCameraSection", "Camera"), _cameraFoldout);
				if (_cameraFoldout) DrawCameraSection();
			});

			YanKInspectorGUI.DrawGroupBox(() =>
			{
				_sceneFoldout = YanKInspectorGUI.DrawFoldoutHeader(
					YanKLocalization.L("scSceneSection", "Scene"), _sceneFoldout);
				if (_sceneFoldout) DrawSceneSection();
			});

			EditorGUILayout.EndScrollView();

			if (GUI.changed)
			{
				EditorUtility.SetDirty(sc);
			}
		}

		private void DrawControllerSelector()
		{
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField(
				YanKLocalization.L("scTargetController", "Target"),
				EditorStyles.boldLabel,
				GUILayout.Width(100));
			var newSc = (SceneController)EditorGUILayout.ObjectField(sc, typeof(SceneController), true);
			if (newSc != sc)
			{
				sc = newSc;
				_scScenePath = PathOf(newSc);
			}
			if (GUILayout.Button(YanKLocalization.L("scPing", "Ping"),
				EditorStyles.miniButton, GUILayout.Width(48)))
			{
				if (sc != null) EditorGUIUtility.PingObject(sc.gameObject);
			}
			EditorGUILayout.EndHorizontal();
			GUILayout.Space(4);
		}

		private void DrawAvatarRootRow()
		{
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField(
				YanKLocalization.L("scAvatarRoot", "Avatar"),
				EditorStyles.boldLabel,
				GUILayout.Width(100));

			var newRoot = (Animator)EditorGUILayout.ObjectField(sc.avatarRoot, typeof(Animator), true);

			GUI.enabled = sc.avatarRoot != null;
			if (GUILayout.Button("✕", GUILayout.Width(22), GUILayout.Height(18)))
				newRoot = null;
			GUI.enabled = true;

			// Auto-ref toggle: green = on, red = off.
			var prevBg = GUI.backgroundColor;
			GUI.backgroundColor = _autoRef ? new Color(0.4f, 1f, 0.4f) : new Color(1f, 0.4f, 0.4f);
			var autoRefContent = new GUIContent("A",
				YanKLocalization.L("scAutoRefTip",
					"Auto Reference: automatically find and assign the only humanoid avatar in the scene."));
			if (GUILayout.Button(autoRefContent, GUILayout.Width(22), GUILayout.Height(18)))
				_autoRef = !_autoRef;
			GUI.backgroundColor = prevBg;

			EditorGUILayout.EndHorizontal();

			if (newRoot != sc.avatarRoot)
			{
				Undo.RecordObject(sc, "Change Avatar Root");
				sc.avatarRoot = newRoot;
				sc.avatarRootName = newRoot != null ? newRoot.gameObject.name : null;
				sc.avatarHomeCaptured = false;
				sc.avatarOffset = Vector3.zero;
					if (sc.avatarRoot != null)
				{
					sc.avatarHomePosition = sc.avatarRoot.transform.position;
					sc.avatarHomeCaptured = true;
				}
				else if (sc.cameraPivot != null)
				{
					// Avatar cleared — reset pivot to default eye-level so orbit still works.
					sc.cameraPivot.position = new Vector3(0f, 1f, 0f);
				}
				SceneControllerRig.EnsureCameraRig(sc);
				EditorUtility.SetDirty(sc);
			}

			GUILayout.Space(4);
		}

		private void DrawMovementInputSection()
		{
			EditorGUILayout.HelpBox(
				YanKLocalization.L("scInputHelp",
					"Hold Right-Click to look around.\n" +
					"WASD / QE move the camera (Free-Fly, RMB held) or move the avatar (no RMB / Orbit).\n" +
					"Hold Shift in Free-Fly for 1.5× speed. Scroll wheel zooms distance in Orbit."),
				MessageType.None);

			float ms = EditorGUILayout.Slider(YanKLocalization.L("scMouseSens", "Mouse Sensitivity"), sc.mouseSensitivity, 0.1f, 10f);
			float mv = EditorGUILayout.Slider(YanKLocalization.L("scMoveSpeed", "Move Speed"), sc.moveSpeed, 0.1f, 10f);
			float vy = EditorGUILayout.Slider(YanKLocalization.L("scVertSpeed", "Vertical Speed"), sc.verticalSpeed, 0.1f, 10f);
			if (!Mathf.Approximately(ms, sc.mouseSensitivity) ||
			    !Mathf.Approximately(mv, sc.moveSpeed) ||
			    !Mathf.Approximately(vy, sc.verticalSpeed))
			{
				Undo.RecordObject(sc, "Movement Input Settings");
				sc.mouseSensitivity = ms;
				sc.moveSpeed = mv;
				sc.verticalSpeed = vy;
			}

			bool inv = EditorGUILayout.Toggle(YanKLocalization.L("scInvertMouseY", "Invert Mouse Y"), sc.invertMouseY);
			if (inv != sc.invertMouseY)
			{
				Undo.RecordObject(sc, "Invert Mouse Y");
				sc.invertMouseY = inv;
			}
		}
	}

	[CustomEditor(typeof(SceneController))]
	internal class SceneControllerStubInspector : Editor
	{
		public override void OnInspectorGUI()
		{
			EditorGUILayout.HelpBox(
				YanKLocalization.L("scStubInfo",
					"The controller UI opens in its own window, leaving the Inspector free."),
				MessageType.Info);
			if (GUILayout.Button(YanKLocalization.L("scOpenWindow", "Open Scene Controller Window")))
			{
				SceneControllerEditor.OpenFor((SceneController)target);
			}
		}
	}
}
