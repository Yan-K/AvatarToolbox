using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace YanK
{
	public enum AutoMoveMode
	{
		PingPongX,
		PingPongY,
		PingPongZ,
		CircleXZ,
		CircleXY
	}

	public enum BoneTarget
	{
		Head,
		Neck,
		Chest,
		Spine,
		Hips,
		LeftHand,
		RightHand,
		LeftFoot,
		RightFoot,
		LeftUpperArm,
		RightUpperArm
	}

	/// <summary>Index-parallel mapping from <see cref="BoneTarget"/> to <see cref="HumanBodyBones"/>.</summary>
	internal static class BoneTargetMap
	{
		public static readonly HumanBodyBones[] ToHumanBone =
		{
			HumanBodyBones.Head,
			HumanBodyBones.Neck,
			HumanBodyBones.Chest,
			HumanBodyBones.Spine,
			HumanBodyBones.Hips,
			HumanBodyBones.LeftHand,
			HumanBodyBones.RightHand,
			HumanBodyBones.LeftFoot,
			HumanBodyBones.RightFoot,
			HumanBodyBones.LeftUpperArm,
			HumanBodyBones.RightUpperArm,
		};
	}

	public enum SkyboxKind
	{
		Color,
		Procedural,
		Custom
	}

	/// <summary>How the Default camera responds to user input.</summary>
	public enum CameraControlMode
	{
		/// <summary>RMB orbits around the inspected bone; WASD pans the pivot.</summary>
		Orbit,
		/// <summary>RMB aims, WASD flies the camera in 6DOF (no pivot orbit).</summary>
		FreeFly,
		/// <summary>SceneController does NOT read input or write camera transforms.
		/// Use this for compatibility with external camera controllers.</summary>
		Off
	}

	/// <summary>Whether to interfere with externally-injected camera controllers.</summary>
	public enum ExternalCameraControlMode
	{
		/// <summary>Leave any third-party camera controllers alone.</summary>
		Allow,
		/// <summary>Strip and disable third-party camera controllers on our managed cameras each tick.</summary>
		Block
	}

	[Serializable]
	public class CustomCameraEntry
	{
		public string name;
		public GameObject gameObject;
	}

	/// <summary>
	/// Snapshot of scene-related settings so the "Recommend Settings" action can be reverted.
	/// </summary>
	[Serializable]
	public class SceneControllerSnapshot
	{
		public bool valid;

		// Skybox
		public Material skyboxMaterial;
		public Color ambientSkyColor;
		public UnityEngine.Rendering.AmbientMode ambientMode;

		// Fog
		public bool fog;
		public Color fogColor;
		public FogMode fogMode;
		public float fogDensity;
		[FormerlySerializedAs("fogStart")] public float fogStartDistance;
		[FormerlySerializedAs("fogEnd")] public float fogEndDistance;

		// SceneController state
		public bool dirLightEnabled;
		public float dirLightHorizontal;
		public float dirLightVertical;

		public bool debugFloorEnabled;
		public bool skyboxEnabled;
		public SkyboxKind skyboxKind;
		public Color skyboxColor;

		public bool postProcessingEnabled;
		public ScriptableObject postProcessingProfile;
	}

	/// <summary>
	/// Yan-K Scene Controller — a scene-resident editor tool that lets you control
	/// avatar position, camera orbit, lighting and post-processing from the Inspector.
	/// Lives in both Edit Mode and Play Mode (<see cref="ExecuteAlways"/>).
	/// </summary>
	[ExecuteAlways]
	[DisallowMultipleComponent]
	[AddComponentMenu("")]
	public class SceneController : MonoBehaviour
	{
		public const string RootName = "Yan-K Scene Controller";
		public const string CameraPivotName = "CameraPivot";
		public const string DefaultCameraName = "DefaultCamera";
		public const string FreeFlyCamera = "FreeFlyCamera";
		public const string DirLightName = "DirectionalLight";
		public const string PointLightsRootName = "RotatingPointLights";
		public const string PostProcessVolumeName = "PostProcessVolume";

		// ----- Avatar -----
		public Animator avatarRoot;
		public string avatarRootName;       // last known name, used to re-link after runtime edits
		public Vector3 avatarHomePosition;
		public bool avatarHomeCaptured;
		public Vector3 avatarOffset;        // x/y/z slider values

		// Tracks the slider offset actually written to the avatar transform so the tool
		// only applies DELTAS each tick, letting the user drag the avatar with scene gizmos.
		[NonSerialized] private Vector3 _lastAppliedOffset;
		[NonSerialized] private bool _lastAppliedOffsetValid;

		public bool autoMoveEnabled;
		public AutoMoveMode autoMoveMode = AutoMoveMode.PingPongX;
		public float autoMoveSpeed = 1f;
		public float autoMoveRadius = 0.5f;

		// ----- Camera -----
		// Camera mode — Orbit on Default, FreeFly is always used for custom cameras.
		public CameraControlMode cameraMode = CameraControlMode.Orbit;

		public BoneTarget boneTarget = BoneTarget.Head;
		public Transform boneTargetOverride;
		public float cameraDistance = 1.5f;
		public float cameraYaw = 0f;
		public float cameraPitch = 0f;
		public float cameraFov = 60f;
		// Additional pan offset applied on top of the avatar-tracking pivot position.
		// Written by MMB pan input; reset when avatar changes or position is reset.
		public Vector3 cameraPivotOffset;

		public Transform cameraPivot;
		public GameObject defaultCameraGo;
		public GameObject freeFlyCamera;      // built-in Free Fly camera; auto-restored if deleted
		public string activeCustomCameraName; // empty == DefaultCamera
		[NonSerialized] public List<CustomCameraEntry> sceneCustomCameras = new List<CustomCameraEntry>();

		// ----- Movement input (play mode) -----
		public float mouseSensitivity = 2f;
		public float moveSpeed = 1f;
		public float verticalSpeed = 1f;
		public bool invertMouseY;

		// When set to Block, strip third-party camera controller injections each tick so our control isn't fought over.
		public ExternalCameraControlMode externalCameraControl = ExternalCameraControlMode.Allow;

		// ----- Directional light -----
		public bool dirLightEnabled;
		public float dirLightHorizontal = 120f;
		public float dirLightVertical = 50f;
		public Color dirLightColor = Color.white;
		public float dirLightIntensity = 1f;
		public LightShadows dirLightShadows = LightShadows.None;
		public float dirLightShadowStrength = 0.75f;
		public Light directionalLight;

		// ----- Point lights -----
		public bool pointLightsEnabled;
		public float pointLightsRadius = 1.5f;
		public float pointLightsHeight = 1.5f;
		public float pointLightsRotationSpeed = 30f;
		public float pointLightCurrentAngle;
		public LightShadows pointLightsShadows = LightShadows.None;
		public float pointLightsShadowStrength = 1f;
		public Color[] pointLightColors = new Color[]
		{
			new Color(1f, 0.3f, 0.3f),
			new Color(0.3f, 1f, 0.3f),
			new Color(0.3f, 0.5f, 1f),
			new Color(1f, 1f, 0.3f),
			new Color(1f, 0.3f, 1f)
		};
		public float[] pointLightIntensities = new float[] { 1f, 1f, 1f, 1f, 1f };
		public Transform pointLightsRoot;
		public Light[] pointLights = new Light[5];

		// ----- Scene section gating -----
		public bool sceneSettingsEnabled;

		// ----- Post processing -----
		public bool postProcessingEnabled;
		public ScriptableObject postProcessingProfile; // PostProcessProfile
		public GameObject postProcessVolumeGo;

		// ----- Skybox -----
		public bool skyboxEnabled;
		public SkyboxKind skyboxKind = SkyboxKind.Color;
		public Color skyboxColor = new Color(50f / 255f, 50f / 255f, 50f / 255f, 1f);
		public Material skyboxProceduralMaterial;   // shipped YSC_Skybox.mat (Skybox/Procedural)
		public Material skyboxCustomMaterial;       // user-provided

		// ----- Fog -----
		public bool fogEnabled;
		public Color fogColor = new Color(50f / 255f, 50f / 255f, 50f / 255f, 1f);
		public FogMode fogMode = FogMode.Linear;
		public float fogDensity = 0.01f;
		public float fogLinearStart = 8f;
		public float fogLinearEnd = 30f;

		// ----- Debug Floor -----
		public bool debugFloorEnabled;
		public GameObject debugFloorGo;
		public Color debugFloorBaseColor = new Color(50f / 255f, 50f / 255f, 50f / 255f, 1f);
		public Color debugFloorLineColor = new Color(120f / 255f, 120f / 255f, 120f / 255f, 1f);

		// ----- Reflection Probe -----
		public const string ReflectionProbeName = "ReflectionProbe";
		public bool reflectionProbeEnabled;
		public Cubemap reflectionProbeCubemap;
		public float reflectionProbeIntensity = 1f;
		public ReflectionProbe reflectionProbe;

		// ----- Internal time tracking -----
		[NonSerialized] private float _lastTickTime;

		// ----- Revert snapshot (Recommend Settings) -----
		[NonSerialized] public SceneControllerSnapshot revertSnapshot;

		// ============================================================
		// Lifecycle
		// ============================================================

		private void OnEnable()
		{
			_lastTickTime = GetTime();
			TryRelinkAvatarByName();
			RefreshSceneCustomCameras();
#if UNITY_EDITOR
			UnityEditor.EditorApplication.update -= EditorTick;
			UnityEditor.EditorApplication.update += EditorTick;
#endif
		}

		/// <summary>
		/// If the cached avatar reference was destroyed / recreated by another tool,
		/// try to find a new GameObject with the same name that carries an Animator
		/// and relink to it so the user isn't stranded.
		/// </summary>
		public void TryRelinkAvatarByName()
		{
			if (avatarRoot != null)
			{
				avatarRootName = avatarRoot.gameObject.name;
				return;
			}
			if (string.IsNullOrEmpty(avatarRootName)) return;

			var go = GameObject.Find(avatarRootName);
			if (go == null) return;
			var anim = go.GetComponent<Animator>() ?? go.GetComponentInChildren<Animator>(true);
			if (anim != null)
			{
				avatarRoot = anim;
				avatarHomeCaptured = false;
				_lastAppliedOffsetValid = false;
			}
		}

		private void OnDisable()
		{
#if UNITY_EDITOR
			UnityEditor.EditorApplication.update -= EditorTick;
#endif
		}

		// In Play mode, we run input/logic in Update but defer the camera rig pose
		// update to LateUpdate so the camera follows the *final* animated position
		// of the avatar each frame — otherwise the camera lags one frame behind the
		// Animator which looks like jitter, especially while scrolling to zoom.
		private void Update()
		{
			if (!Application.isPlaying) return;
			if (externalCameraControl == ExternalCameraControlMode.Block) SceneControllerExternalBlocker.Tick(this);
			SceneControllerInput.HandleInput(this, Time.deltaTime);
			ApplyAvatarMovement(Time.deltaTime);
			UpdatePointLightsRotation(Time.deltaTime);
			UpdateDirectionalLight();
			ApplyFogSettings();
			ApplyDebugFloorMaterial();
		}

		private void LateUpdate()
		{
			if (!Application.isPlaying) return;
			UpdateCameraRig();
		}

		// In Edit mode, EditorApplication.update is the only reliable tick.
		private void EditorTick()
		{
			if (Application.isPlaying) return;
			if (this == null) return;
			float now = GetTime();
			float dt = Mathf.Max(0f, now - _lastTickTime);
			_lastTickTime = now;
			if (dt > 0.5f) dt = 0f; // big gaps after recompile etc.
			Tick(dt);
		}

		private static float GetTime()
		{
#if UNITY_EDITOR
			return (float)UnityEditor.EditorApplication.timeSinceStartup;
#else
			return Time.realtimeSinceStartup;
#endif
		}

		// ============================================================
		// Per-frame logic
		// ============================================================

		private void Tick(float dt)
		{
			if (externalCameraControl == ExternalCameraControlMode.Block) SceneControllerExternalBlocker.Tick(this);
			ApplyAvatarMovement(dt);
			UpdateCameraRig();
			UpdatePointLightsRotation(dt);
			UpdateDirectionalLight();
			ApplyFogSettings();
			ApplyDebugFloorMaterial();
		}

		private void ApplyAvatarMovement(float dt)
		{
			if (avatarRoot == null) return;
			if (!avatarHomeCaptured)
			{
				avatarHomePosition = avatarRoot.transform.position;
				avatarHomeCaptured = true;
				_lastAppliedOffset = Vector3.zero;
				_lastAppliedOffsetValid = true;
			}

			if (autoMoveEnabled)
			{
				float t = GetTime() * autoMoveSpeed;
				Vector3 offset = Vector3.zero;
				switch (autoMoveMode)
				{
					case AutoMoveMode.PingPongX: offset.x = Mathf.Sin(t) * autoMoveRadius; break;
					case AutoMoveMode.PingPongY: offset.y = Mathf.Sin(t) * autoMoveRadius; break;
					case AutoMoveMode.PingPongZ: offset.z = Mathf.Sin(t) * autoMoveRadius; break;
					case AutoMoveMode.CircleXZ:
						offset.x = Mathf.Cos(t) * autoMoveRadius;
						offset.z = Mathf.Sin(t) * autoMoveRadius;
						break;
					case AutoMoveMode.CircleXY:
						offset.x = Mathf.Cos(t) * autoMoveRadius;
						offset.y = Mathf.Sin(t) * autoMoveRadius;
						break;
				}
				avatarOffset = offset;
			}

			// Additive: only push the DELTA so the user can still drag the avatar with
			// the scene gizmo without the tool snapping it back each frame.
			Vector3 current = _lastAppliedOffsetValid ? _lastAppliedOffset : Vector3.zero;
			Vector3 delta = avatarOffset - current;
			if (delta.sqrMagnitude > 0f)
			{
				avatarRoot.transform.position += delta;
			}
			_lastAppliedOffset = avatarOffset;
			_lastAppliedOffsetValid = true;
		}

		/// <summary>Resets the avatar to its captured home position and zeroes the slider offset.</summary>
		public void ResetAvatarPosition()
		{
			if (avatarRoot != null && avatarHomeCaptured)
				avatarRoot.transform.position = avatarHomePosition;
			avatarOffset = Vector3.zero;
			_lastAppliedOffset = Vector3.zero;
			_lastAppliedOffsetValid = true;
			autoMoveEnabled = false;
			cameraPivotOffset = Vector3.zero;
		}

		private void UpdateCameraRig()
		{
			if (cameraPivot == null) return;

			// If the avatar reference is null, FREEZE the pivot/camera in place rather
			// than snapping back to the world origin. The user keeps the framing they
			// last had until either the reference is restored (TryRelinkAvatarByName)
			// or they pick a new avatar.
			if (avatarRoot == null) return;

			cameraPivot.position = avatarRoot.transform.position + cameraPivotOffset;
			cameraPivot.rotation = Quaternion.identity;

			var mode = GetEffectiveCameraMode();
			// Off mode hands camera control to whatever external script is driving things.
			if (mode == CameraControlMode.Off) return;

			bool isCustomActive = !string.IsNullOrEmpty(activeCustomCameraName);

			// Only the Default camera in Orbit mode is driven from sliders.
			// Free-Fly mode never rewrites the transform so WASD / mouse input sticks.
			// Custom cameras always free-fly and are children of cameraPivot, so avatar movement
			// carries them automatically (relative offset preserved).
			if (defaultCameraGo != null && !isCustomActive && mode == CameraControlMode.Orbit)
			{
				var t = defaultCameraGo.transform;
				Quaternion rot = Quaternion.Euler(cameraPitch, cameraYaw, 0f);
				Vector3 bonePos = GetActiveBoneTransform()?.position ?? cameraPivot.position;
				// cameraPivotOffset shifts the look-at centre independently of bone position.
				Vector3 lookCenter = bonePos + cameraPivotOffset;
				// Vector3.forward so yaw=0 places the camera in front of the avatar (looking at it).
				Vector3 dir = rot * Vector3.forward;
				t.position = lookCenter + dir * cameraDistance;
				t.LookAt(lookCenter, Vector3.up);
			}

			// FOV is always driven by the slider, in every mode and for every active camera.
			if (isCustomActive)
			{
				var cam = GetActiveCamera();
				if (cam != null) cam.fieldOfView = cameraFov;
			}
			else if (defaultCameraGo != null)
			{
				var cam = defaultCameraGo.GetComponent<Camera>();
				if (cam != null) cam.fieldOfView = cameraFov;
			}
		}

		/// <summary>Effective input mode. Custom cameras are always Free-Fly, except when explicitly Off.</summary>
		public CameraControlMode GetEffectiveCameraMode()
		{
			if (cameraMode == CameraControlMode.Off) return CameraControlMode.Off;
			return string.IsNullOrEmpty(activeCustomCameraName) ? cameraMode : CameraControlMode.FreeFly;
		}

		private void UpdatePointLightsRotation(float dt)
		{
			if (!pointLightsEnabled || pointLightsRoot == null) return;
			if (avatarRoot != null)
			{
				pointLightsRoot.position = avatarRoot.transform.position + Vector3.up * pointLightsHeight;
			}
			pointLightCurrentAngle += pointLightsRotationSpeed * dt;
			pointLightsRoot.rotation = Quaternion.Euler(0f, pointLightCurrentAngle, 0f);
		}

		private void UpdateDirectionalLight()
		{
			if (!dirLightEnabled || directionalLight == null) return;
			directionalLight.transform.rotation = Quaternion.Euler(dirLightVertical, dirLightHorizontal, 0f);
			directionalLight.color = dirLightColor;
			directionalLight.intensity = dirLightIntensity;
			directionalLight.shadows = dirLightShadows;
			directionalLight.shadowStrength = dirLightShadowStrength;
		}

		private void ApplyFogSettings()
		{
			if (!fogEnabled) return;
			RenderSettings.fog = true;
			RenderSettings.fogColor = fogColor;
			RenderSettings.fogMode = fogMode;
			RenderSettings.fogDensity = fogDensity;
			RenderSettings.fogStartDistance = fogLinearStart;
			RenderSettings.fogEndDistance = fogLinearEnd;
		}

		private void ApplyDebugFloorMaterial()
		{
			if (!debugFloorEnabled || debugFloorGo == null) return;
			var mr = debugFloorGo.GetComponent<MeshRenderer>();
			if (mr == null || mr.sharedMaterial == null) return;
			var m = mr.sharedMaterial;
			if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", debugFloorBaseColor);
			if (m.HasProperty("_LineColor")) m.SetColor("_LineColor", debugFloorLineColor);
		}

		public void ApplySkybox()
		{
			if (!skyboxEnabled) return;

			if (skyboxKind == SkyboxKind.Color)
			{
				// Drop skybox material and paint a flat background on the active camera.
				RenderSettings.skybox = null;
				var cam = GetActiveCamera();
				if (cam != null)
				{
					cam.clearFlags = CameraClearFlags.SolidColor;
					cam.backgroundColor = skyboxColor;
				}
				RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
				RenderSettings.ambientLight = skyboxColor;
				DynamicGI.UpdateEnvironment();
				return;
			}

			Material mat = null;
			switch (skyboxKind)
			{
				case SkyboxKind.Procedural: mat = skyboxProceduralMaterial; break;
				case SkyboxKind.Custom:     mat = skyboxCustomMaterial; break;
			}

			if (mat != null)
			{
				RenderSettings.skybox = mat;
				RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox;
				var cam = GetActiveCamera();
				if (cam != null) cam.clearFlags = CameraClearFlags.Skybox;
				DynamicGI.UpdateEnvironment();
			}
		}

		// ============================================================
		// Helpers
		// ============================================================

		public Transform GetActiveBoneTransform()
		{
			if (boneTargetOverride != null) return boneTargetOverride;
			if (avatarRoot == null) return null;

			int idx = (int)boneTarget;
			HumanBodyBones bone = (idx >= 0 && idx < BoneTargetMap.ToHumanBone.Length)
				? BoneTargetMap.ToHumanBone[idx]
				: HumanBodyBones.Head;

			if (!avatarRoot.isHuman) return avatarRoot.transform;
			var t = avatarRoot.GetBoneTransform(bone);
			return t != null ? t : avatarRoot.transform;
		}

		public Camera GetActiveCamera()
		{
			GameObject active = null;
			if (!string.IsNullOrEmpty(activeCustomCameraName))
			{
				var entry = sceneCustomCameras?.Find(e => e != null && e.name == activeCustomCameraName);
				if (entry != null && entry.gameObject != null) active = entry.gameObject;
			}
			if (active == null) active = defaultCameraGo;
			return active != null ? active.GetComponentInChildren<Camera>(true) : null;
		}

		public void RefreshSceneCustomCameras()
		{
			sceneCustomCameras = new List<CustomCameraEntry>();
			if (cameraPivot == null) return;
			foreach (Transform child in cameraPivot)
			{
				if (defaultCameraGo != null && child.gameObject == defaultCameraGo) continue;
				if (child.GetComponent<Camera>() == null) continue;
				sceneCustomCameras.Add(new CustomCameraEntry
				{
					name = child.name,
					gameObject = child.gameObject
				});
			}
		}
	}
}
