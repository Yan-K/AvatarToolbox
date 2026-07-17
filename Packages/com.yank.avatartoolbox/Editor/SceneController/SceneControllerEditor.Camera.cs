using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace YanK
{
	public partial class SceneControllerEditor
	{
		// Camera preset Add/Remove workflow (Add name buffer + Remove selection set).
		private readonly PresetWorkflow<string> _camWorkflow = new PresetWorkflow<string>();

		// Camera that was just created by "Add New" and is pending a name confirmation.
		private GameObject _pendingNewCamera;
		private string _previousActiveCameraName;

		private void OnEnableCamera() { }

		private void DrawCameraSection()
		{
			GUILayout.Space(4);

			// Repair missing built-in cameras (DefaultCamera / FreeFlyCamera) every repaint,
			// then sync active states in case a camera was just rebuilt.
			SceneControllerRig.EnsureCameraRig(sc);
			sc.RefreshSceneCustomCameras();
			ApplyActiveCamera();

			var mode = sc.GetEffectiveCameraMode();
			bool isFreeFly = mode == CameraControlMode.FreeFly;
			bool isOrbit = mode == CameraControlMode.Orbit;
			bool isOff = mode == CameraControlMode.Off;

			// ---- Control mode (3-button toggle: Orbit / Free Fly / Off) ----
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField(YanKLocalization.L("scCamMode", "Control Mode"), GUILayout.Width(EditorGUIUtility.labelWidth));
			int currentIdx = sc.cameraMode == CameraControlMode.Orbit ? 0
				: sc.cameraMode == CameraControlMode.FreeFly ? 1 : 2;
			int modeIdx = GUILayout.Toolbar(
				currentIdx,
				new[]
				{
					YanKLocalization.L("scOrbit", "Orbit"),
					YanKLocalization.L("scFreeFly", "Free Fly"),
					YanKLocalization.L("scCamOff", "Off")
				});
			EditorGUILayout.EndHorizontal();
			var newMode = modeIdx == 0 ? CameraControlMode.Orbit
				: modeIdx == 1 ? CameraControlMode.FreeFly
				: CameraControlMode.Off;
			if (newMode != sc.cameraMode)
			{
				Undo.RecordObject(sc, "Change Camera Mode");
				sc.cameraMode = newMode;
				if (newMode == CameraControlMode.Orbit)
				{
					sc.activeCustomCameraName = "";
					ApplyActiveCamera();
				}
				else if (newMode == CameraControlMode.FreeFly && string.IsNullOrEmpty(sc.activeCustomCameraName))
				{
					sc.activeCustomCameraName = SceneController.FreeFlyCamera;
					SyncFovFromActiveCustomCamera(sc.activeCustomCameraName);
					ApplyActiveCamera();
				}
				// Off: leave activeCustomCameraName as-is so the user can return to it later.
			}

			// ---- External controller block ----
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField(
				YanKLocalization.L("scExtCamMode", "External Controllers"),
				GUILayout.Width(EditorGUIUtility.labelWidth));
			int extIdx = sc.externalCameraControl == ExternalCameraControlMode.Allow ? 0 : 1;
			int newExtIdx = GUILayout.Toolbar(
				extIdx,
				new[]
				{
					YanKLocalization.L("scExtCamAllow", "Allow"),
					YanKLocalization.L("scExtCamBlock", "Block")
				});
			EditorGUILayout.EndHorizontal();
			var newExt = newExtIdx == 0 ? ExternalCameraControlMode.Allow : ExternalCameraControlMode.Block;
			if (newExt != sc.externalCameraControl)
			{
				Undo.RecordObject(sc, "Change External Camera Control");
				sc.externalCameraControl = newExt;
			}

			if (isOff)
			{
				EditorGUILayout.HelpBox(
					YanKLocalization.L("scCamOffHint",
						"Off: Scene Controller will not read input or modify the camera transform.\nUse this when another tool should have full control of the camera."),
					MessageType.Info);
				return;
			}

			GUILayout.Space(8);

			// ---- Inspect Bone + Custom Target (hidden in Free-Fly) ----
			if (!isFreeFly)
			{
				DrawBoneTargetRow();
			}

			YanKInspectorGUI.DrawStyledSeparator();

			// ---- Field of View (always) ----
			float newFov = EditorGUILayout.Slider(YanKLocalization.L("scFov", "Field of View"), sc.cameraFov, 20f, 100f);
			if (SceneControllerMath.Changed(newFov, sc.cameraFov))
			{
				Undo.RecordObject(sc, "Camera FOV");
				sc.cameraFov = newFov;
			}

			// ---- Orbit sliders (Distance / Rotation / Pitch) — hidden in Free-Fly ----
			if (isOrbit)
			{
				EditorGUI.BeginChangeCheck();
				float newDist = EditorGUILayout.Slider(YanKLocalization.L("scDistance", "Distance"), sc.cameraDistance, 0.2f, 20f);
				float newYaw = EditorGUILayout.Slider(YanKLocalization.L("scYaw", "Yaw"), sc.cameraYaw, -180f, 180f);
				float newPitch = EditorGUILayout.Slider(YanKLocalization.L("scPitch", "Pitch"), sc.cameraPitch, -89f, 89f);
				if (EditorGUI.EndChangeCheck())
				{
					Undo.RecordObject(sc, "Camera Orbit");
					sc.cameraDistance = newDist;
					sc.cameraYaw = newYaw;
					sc.cameraPitch = newPitch;
				}
			}

			// ---- Custom Cameras — only shown in Free-Fly mode (Orbit hides it) ----
			if (isFreeFly)
			{
				DrawCustomCameras();
			}
		}

		private void DrawBoneTargetRow()
		{
			using (new EditorGUI.DisabledScope(sc.boneTargetOverride != null || sc.avatarRoot == null))
			{
				EditorGUILayout.LabelField(YanKLocalization.L("scBoneTarget", "Bone to Inspect"));
				int curBone = (int)sc.boneTarget;
				int r1Sel = curBone < 5 ? curBone : -1;
				int r2Sel = curBone >= 5 ? curBone - 5 : -1;
				int newR1 = GUILayout.Toolbar(r1Sel, SceneControllerConstants.BoneRow1Labels, EditorStyles.miniButton);
				int newR2 = GUILayout.Toolbar(r2Sel, SceneControllerConstants.BoneRow2Labels, EditorStyles.miniButton);
				int newBoneIdx = -1;
				if (newR1 != r1Sel) newBoneIdx = newR1;
				else if (newR2 != r2Sel) newBoneIdx = newR2 + 5;
				if (newBoneIdx >= 0)
				{
					Undo.RecordObject(sc, "Change Bone Target");
					sc.boneTarget = (BoneTarget)newBoneIdx;
					sc.cameraPivotOffset = Vector3.zero;
				}
			}

			EditorGUILayout.BeginHorizontal();
			var newOverride = (Transform)EditorGUILayout.ObjectField(
				YanKLocalization.L("scBoneOverride", "Custom Target (override)"),
				sc.boneTargetOverride, typeof(Transform), true);
			GUI.enabled = sc.boneTargetOverride != null;
			if (GUILayout.Button("✕", GUILayout.Width(22), GUILayout.Height(18)))
				newOverride = null;
			GUI.enabled = true;
			EditorGUILayout.EndHorizontal();
			if (newOverride != sc.boneTargetOverride)
			{
				Undo.RecordObject(sc, "Change Bone Override");
				sc.boneTargetOverride = newOverride;
				sc.cameraPivotOffset = Vector3.zero;
			}
		}

		// ========================================================================
		// Custom camera picker — flat searchable list + Add / Remove.
		// ========================================================================

		private void DrawCustomCameras()
		{
			EditorGUILayout.LabelField(YanKLocalization.L("scCustomCameras", "Custom Cameras"), EditorStyles.boldLabel);

			sc.RefreshSceneCustomCameras();

			DrawCustomCameraPickerRow();

			GUILayout.Space(4);

			// ---- Add-name entry row (only shown while Add is open) ----
			if (_camWorkflow.addOpen)
			{
				var act = SceneControllerPresetUI.DrawAddRow(ref _camWorkflow.addName);
				if (act == SceneControllerPresetUI.AddAction.Save)
				{
					ConfirmPendingCamera(_camWorkflow.addName.Trim());
					_camWorkflow.addOpen = false;
					GUIUtility.ExitGUI();
				}
				else if (act == SceneControllerPresetUI.AddAction.Cancel)
				{
					CancelPendingCamera();
					_camWorkflow.addOpen = false;
					GUIUtility.ExitGUI();
				}
			}

			if (_camWorkflow.removeOpen && !_camWorkflow.addOpen) DrawCamRemovePanel();
		}

		private void DrawCustomCameraPickerRow()
		{
			string currentLabel = string.IsNullOrEmpty(sc.activeCustomCameraName)
				? YanKLocalization.L("scDefaultCam", "(Default Camera)")
				: sc.activeCustomCameraName;

			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField(YanKLocalization.L("scActiveCam", "Active"), GUILayout.Width(60));

			var items = BuildCustomCameraItems();

			using (new EditorGUI.DisabledScope(_camWorkflow.addOpen))
			{
				YanKSearchableDropdown.Field(currentLabel, items, HandleCameraPick, GUILayout.ExpandWidth(true));
			}

			if (GUILayout.Button(YanKLocalization.L("scPing", "Ping"), EditorStyles.miniButton, GUILayout.Width(48)))
			{
				var go = GetActiveCameraGo();
				if (go != null) EditorGUIUtility.PingObject(go);
			}

			// Add / Remove buttons.
			if (!_camWorkflow.addOpen)
			{
				if (GUILayout.Button(YanKLocalization.L("scAddNewPreset", "Add New"),
					EditorStyles.miniButton, GUILayout.Width(70)))
				{
					string newName = NextSceneCameraName("Camera");
					_previousActiveCameraName = sc.activeCustomCameraName;
					_pendingNewCamera = CreateAndActivateNewCamera(newName);
					_camWorkflow.OpenAdd(newName);
					if (_pendingNewCamera != null) EditorGUIUtility.PingObject(_pendingNewCamera);
				}

				using (new EditorGUI.DisabledScope(_camWorkflow.addOpen))
				{
					if (GUILayout.Button(YanKLocalization.L("scRemove", "Remove"),
						EditorStyles.miniButton, GUILayout.Width(70)))
					{
						_camWorkflow.ToggleRemove();
					}
				}
			}
			EditorGUILayout.EndHorizontal();
		}

		private List<YanKSearchableDropdown.Item> BuildCustomCameraItems()
		{
			var items = new List<YanKSearchableDropdown.Item>(sc.sceneCustomCameras.Count);
			foreach (var entry in sc.sceneCustomCameras)
			{
				if (entry == null || string.IsNullOrEmpty(entry.name)) continue;
				items.Add(new YanKSearchableDropdown.Item
				{
					label = entry.name,
					payload = new CamPick { kind = CamPickKind.Scene, name = entry.name }
				});
			}
			return items;
		}

		private void HandleCameraPick(object payload)
		{
			var pick = (CamPick)payload;
			Undo.RecordObject(sc, "Switch Active Camera");
			sc.activeCustomCameraName = pick.name;
			SyncFovFromActiveCustomCamera(pick.name);
			ApplyActiveCamera();
		}

		/// <summary>Reads the FOV from a named scene camera and writes it into <see cref="SceneController.cameraFov"/> so the slider reflects the camera's actual value.</summary>
		private void SyncFovFromActiveCustomCamera(string cameraName)
		{
			var entry = sc.sceneCustomCameras?.Find(e => e != null && e.name == cameraName);
			if (entry?.gameObject == null) return;
			var cam = entry.gameObject.GetComponentInChildren<Camera>(true);
			if (cam != null) sc.cameraFov = cam.fieldOfView;
		}

		private void DrawCamRemovePanel()
		{
			var removable = new List<SceneControllerPresetUI.RemoveEntry<string>>();
			foreach (var entry in sc.sceneCustomCameras)
			{
				if (entry == null || string.IsNullOrEmpty(entry.name)) continue;
				if (entry.name == SceneController.FreeFlyCamera) continue; // built-in, protected
				removable.Add(new SceneControllerPresetUI.RemoveEntry<string>(entry.name, entry.name));
			}

			var action = SceneControllerPresetUI.DrawRemovePanel(
				removable, _camWorkflow.removeSel, out var toRemove);

			if (action == SceneControllerPresetUI.RemoveAction.Confirm)
			{
				foreach (var n in toRemove)
				{
					var ent = sc.sceneCustomCameras.Find(e => e != null && e.name == n);
					if (ent?.gameObject != null)
					{
						if (sc.activeCustomCameraName == ent.name) sc.activeCustomCameraName = "";
						Undo.DestroyObjectImmediate(ent.gameObject);
					}
				}
				sc.RefreshSceneCustomCameras();
				_camWorkflow.CloseAll();
				GUIUtility.ExitGUI();
			}
			else if (action == SceneControllerPresetUI.RemoveAction.Cancel)
			{
				_camWorkflow.CloseAll();
			}
		}

		// ========================================================================
		// Helpers
		// ========================================================================

		private GameObject GetActiveCameraGo()
		{
			if (string.IsNullOrEmpty(sc.activeCustomCameraName)) return sc.defaultCameraGo;
			var ent = sc.sceneCustomCameras.Find(e => e.name == sc.activeCustomCameraName);
			return ent?.gameObject;
		}

		private void ApplyActiveCamera()
		{
			if (sc.defaultCameraGo != null)
				sc.defaultCameraGo.SetActive(string.IsNullOrEmpty(sc.activeCustomCameraName));

			foreach (var entry in sc.sceneCustomCameras)
			{
				if (entry?.gameObject == null) continue;
				entry.gameObject.SetActive(entry.name == sc.activeCustomCameraName);
			}
		}

		private string NextSceneCameraName(string baseName)
		{
			var taken = new HashSet<string>();
			foreach (var e in sc.sceneCustomCameras)
				if (e != null && !string.IsNullOrEmpty(e.name)) taken.Add(e.name);
			if (!taken.Contains(baseName)) return baseName;
			int i = 1;
			while (taken.Contains(baseName + i)) i++;
			return baseName + i;
		}

		private GameObject CreateAndActivateNewCamera(string name)
		{
			SceneControllerRig.EnsureCameraRig(sc);
			var go = new GameObject(name);
			Undo.RegisterCreatedObjectUndo(go, "Add Custom Camera");
			go.transform.SetParent(sc.cameraPivot, false);
			// Start at the default camera position so the view is immediately useful.
			if (sc.defaultCameraGo != null)
				go.transform.SetPositionAndRotation(
					sc.defaultCameraGo.transform.position,
					sc.defaultCameraGo.transform.rotation);
			var cam = go.AddComponent<Camera>();
			cam.fieldOfView = sc.cameraFov;
			if (sc.defaultCameraGo != null)
			{
				var src = sc.defaultCameraGo.GetComponent<Camera>();
				if (src != null)
				{
					cam.clearFlags      = src.clearFlags;
					cam.backgroundColor = src.backgroundColor;
					cam.nearClipPlane   = src.nearClipPlane;
					cam.farClipPlane    = src.farClipPlane;
					cam.cullingMask     = src.cullingMask;
				}
			}
			if (PostProcessingReflection.IsAvailable)
				PostProcessingReflection.EnsureLayer(go, 0);
			sc.RefreshSceneCustomCameras();
			sc.activeCustomCameraName = name;
			ApplyActiveCamera();
			return go;
		}

		private void ConfirmPendingCamera(string newName)
		{
			if (_pendingNewCamera == null) return;
			string oldName = _pendingNewCamera.name;
			// Ensure the typed name is unique among existing scene cameras.
			var taken = new HashSet<string>();
			foreach (var e in sc.sceneCustomCameras)
				if (e != null && e.name != oldName && !string.IsNullOrEmpty(e.name)) taken.Add(e.name);
			string uniqueName = newName;
			if (taken.Contains(uniqueName))
			{
				int i = 1;
				while (taken.Contains(uniqueName + i)) i++;
				uniqueName += i;
			}
			Undo.RecordObject(_pendingNewCamera, "Rename Camera");
			_pendingNewCamera.name = uniqueName;
			sc.activeCustomCameraName = uniqueName;
			sc.RefreshSceneCustomCameras();
			ApplyActiveCamera();
			_pendingNewCamera = null;
			_previousActiveCameraName = null;
		}

		private void CancelPendingCamera()
		{
			if (_pendingNewCamera != null)
			{
				Undo.DestroyObjectImmediate(_pendingNewCamera);
				_pendingNewCamera = null;
			}
			sc.activeCustomCameraName = _previousActiveCameraName ?? "";
			sc.RefreshSceneCustomCameras();
			ApplyActiveCamera();
			_previousActiveCameraName = null;
		}

		private enum CamPickKind { Scene }
		private struct CamPick { public CamPickKind kind; public string name; }
	}
}
