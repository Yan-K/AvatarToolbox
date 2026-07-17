using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace YanK
{
	public partial class SceneControllerEditor
	{
		// Directional-light preset Add/Remove workflow.
		private readonly PresetWorkflow<string> _dirWorkflow = new PresetWorkflow<string>();

		// Nullable cache — lazy-reloaded on first read and after mutation.
		private List<SceneControllerPresets.DirLightPreset> _dirPresetsMerged;
		private HashSet<string> _dirBuiltInNames;
		private List<SceneControllerPresets.DirLightPreset> DirPresets =>
			_dirPresetsMerged ??= SceneControllerPresets.LoadMergedDirLightPresets(out _dirBuiltInNames);

		private void OnEnableScene()
		{
			_dirPresetsMerged = null; // force lazy reload on first use
		}

		private void ReloadDirPresets()
		{
			_dirPresetsMerged = null;
		}

		// ================================================================
		// Scene section — gated behind an Enable button (NOT a checkbox).
		// ================================================================

		private void DrawSceneSection()
		{
			GUILayout.Space(4);

			if (!sc.sceneSettingsEnabled)
			{
				EditorGUILayout.HelpBox(
					YanKLocalization.L("scSceneDisabledHint",
						"Scene settings are disabled. Click Enable to apply the recommended defaults: " +
						"a gray skybox, linear fog, a directional light, a debug floor, and Basic post-processing. " +
						"A snapshot is saved so you can revert the changes."),
					MessageType.None);
				if (GUILayout.Button(YanKLocalization.L("scEnableScene", "Enable Scene Settings"), GUILayout.Height(28)))
				{
					TurnOnSceneSettings();
					GUIUtility.ExitGUI();
				}
				return;
			}

			// Header row with Disable button on the right.
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField(
				YanKLocalization.L("scSceneEnabled", "Scene Settings Enabled"), EditorStyles.boldLabel);
			GUILayout.FlexibleSpace();
			if (GUILayout.Button(YanKLocalization.L("scDisableScene", "Disable"), GUILayout.Width(90)))
			{
				TurnOffSceneSettings();
				GUIUtility.ExitGUI();
			}
			EditorGUILayout.EndHorizontal();

			DrawDarkLightQuickPresets();

			YanKInspectorGUI.DrawStyledSeparator();
			DrawSkybox();
			YanKInspectorGUI.DrawStyledSeparator();
			DrawFog();
			YanKInspectorGUI.DrawStyledSeparator();
			DrawDebugFloor();
			YanKInspectorGUI.DrawStyledSeparator();
			DrawDirectionalLight();
			YanKInspectorGUI.DrawStyledSeparator();
			DrawPointLights();
			YanKInspectorGUI.DrawStyledSeparator();
			DrawReflectionProbe();
			YanKInspectorGUI.DrawStyledSeparator();
			DrawPostProcessingSection();
		}

		private void DrawDarkLightQuickPresets()
		{
			EditorGUILayout.BeginHorizontal();
			if (GUILayout.Button(YanKLocalization.L("scSceneDark", "Dark"), EditorStyles.miniButton))
			{
				Undo.RecordObject(sc, "Scene Dark Mode");
				sc.skyboxColor = SceneControllerConstants.DarkBase;
				sc.fogColor = SceneControllerConstants.DarkBase;
				sc.ApplySkybox();
				RenderSettings.fogColor = sc.fogColor;
				sc.debugFloorBaseColor = SceneControllerConstants.DarkBase;
				sc.debugFloorLineColor = SceneControllerConstants.DarkLine;
				SceneControllerRig.ApplyDebugFloorColors(sc);
			}
			if (GUILayout.Button(YanKLocalization.L("scSceneLight", "Light"), EditorStyles.miniButton))
			{
				Undo.RecordObject(sc, "Scene Light Mode");
				sc.skyboxColor = SceneControllerConstants.LightBase;
				sc.fogColor = SceneControllerConstants.LightBase;
				sc.ApplySkybox();
				RenderSettings.fogColor = sc.fogColor;
				sc.debugFloorBaseColor = SceneControllerConstants.LightFloor;
				sc.debugFloorLineColor = SceneControllerConstants.LightLine;
				SceneControllerRig.ApplyDebugFloorColors(sc);
			}
			EditorGUILayout.EndHorizontal();
		}

		// ---------------- Enable / disable bookkeeping ----------------

		private void TurnOnSceneSettings()
		{
			// Offer to disable scene-external lights first so the fresh lighting
			// rig starts from a known blank state.
			SceneControllerMenu.ScanAndOfferToDisableExternalLights(sc);

			Undo.RecordObject(sc, "Enable Scene Settings");
			CaptureSceneSnapshot(sc);
			sc.sceneSettingsEnabled = true;
			// Auto-apply Yan-K's recommended defaults so the user doesn't have to
			// toggle each sub-section individually to see anything.
			SceneControllerRig.ApplyRecommendedDefaults(sc);

			// Pick the "Basic" profile if it ships with the package.
			if (PostProcessingReflection.IsAvailable)
			{
				var profiles = PpProfiles;
				var basic = profiles.Find(p => p != null && p.name == "BasicNeutral");
				if (basic != null) sc.postProcessingProfile = basic;
				else if (profiles.Count > 0) sc.postProcessingProfile = profiles[0];
				sc.postProcessingEnabled = true;
				SceneControllerRig.EnsurePostProcessVolume(sc);
			}
		}

		private void TurnOffSceneSettings()
		{
			Undo.RecordObject(sc, "Disable Scene Settings");
			sc.sceneSettingsEnabled = false;
			RestoreSceneSnapshot(sc);
			if (sc.directionalLight != null) SceneControllerRig.DestroyDirectionalLight(sc);
			if (sc.pointLightsRoot != null) SceneControllerRig.DestroyPointLights(sc);
			if (sc.debugFloorGo != null) SceneControllerRig.DestroyDebugFloor(sc);
			if (sc.reflectionProbe != null) SceneControllerRig.DestroyReflectionProbe(sc);
			if (sc.postProcessVolumeGo != null) SceneControllerRig.SetPostProcessVolumeActive(sc, false);
			sc.skyboxEnabled = false;
			sc.dirLightEnabled = false;
			sc.pointLightsEnabled = false;
			sc.debugFloorEnabled = false;
			sc.fogEnabled = false;
			sc.postProcessingEnabled = false;
		}

		private static void CaptureSceneSnapshot(SceneController sc)
		{
			sc.revertSnapshot = new SceneControllerSnapshot
			{
				valid = true,
				skyboxMaterial = RenderSettings.skybox,
				ambientSkyColor = RenderSettings.ambientSkyColor,
				ambientMode = RenderSettings.ambientMode,
				fog = RenderSettings.fog,
				fogColor = RenderSettings.fogColor,
				fogMode = RenderSettings.fogMode,
				fogDensity = RenderSettings.fogDensity,
				fogStartDistance = RenderSettings.fogStartDistance,
				fogEndDistance = RenderSettings.fogEndDistance,
				dirLightEnabled = sc.dirLightEnabled,
				dirLightHorizontal = sc.dirLightHorizontal,
				dirLightVertical = sc.dirLightVertical,
				debugFloorEnabled = sc.debugFloorEnabled,
				skyboxEnabled = sc.skyboxEnabled,
				skyboxKind = sc.skyboxKind,
				skyboxColor = sc.skyboxColor,
				postProcessingEnabled = sc.postProcessingEnabled,
				postProcessingProfile = sc.postProcessingProfile,
			};
		}

		private static void RestoreSceneSnapshot(SceneController sc)
		{
			var s = sc.revertSnapshot;
			if (s == null || !s.valid) return;

			RenderSettings.skybox = s.skyboxMaterial;
			RenderSettings.ambientMode = s.ambientMode;
			RenderSettings.ambientSkyColor = s.ambientSkyColor;
			RenderSettings.fog = s.fog;
			RenderSettings.fogColor = s.fogColor;
			RenderSettings.fogMode = s.fogMode;
			RenderSettings.fogDensity = s.fogDensity;
			RenderSettings.fogStartDistance = s.fogStartDistance;
			RenderSettings.fogEndDistance = s.fogEndDistance;

			sc.revertSnapshot = null;
		}

		// ---------------- Skybox ----------------

		private void DrawSkybox()
		{
			bool newEnabled = EditorGUILayout.ToggleLeft(
				YanKLocalization.L("scSkybox", "Skybox"), sc.skyboxEnabled);
			if (newEnabled != sc.skyboxEnabled)
			{
				Undo.RecordObject(sc, "Toggle Skybox");
				sc.skyboxEnabled = newEnabled;
				if (newEnabled) sc.ApplySkybox();
			}
			if (!sc.skyboxEnabled) return;

			var newKind = (SkyboxKind)EditorGUILayout.EnumPopup(
				YanKLocalization.L("scSkybox", "Skybox"), sc.skyboxKind);
			if (newKind != sc.skyboxKind)
			{
				Undo.RecordObject(sc, "Change Skybox Kind");
				sc.skyboxKind = newKind;
				sc.ApplySkybox();
			}

			switch (sc.skyboxKind)
			{
				case SkyboxKind.Color:
					var c = EditorGUILayout.ColorField(YanKLocalization.L("scColor", "Color"), sc.skyboxColor);
					if (c != sc.skyboxColor)
					{
						Undo.RecordObject(sc, "Change Skybox Color");
						sc.skyboxColor = c;
						sc.ApplySkybox();
					}
					break;
				case SkyboxKind.Procedural:
					// Uses shipped YSC_Skybox material — no user-facing material slot.
					EditorGUILayout.HelpBox(
						YanKLocalization.L("scSkyboxProcHint",
							"Using built-in YSC_Skybox material (Skybox/Procedural)."),
						MessageType.None);
					break;
				case SkyboxKind.Custom:
					var newMat = (Material)EditorGUILayout.ObjectField(
						YanKLocalization.L("scSkyboxCustomMat", "Skybox Material"),
						sc.skyboxCustomMaterial, typeof(Material), false);
					if (newMat != sc.skyboxCustomMaterial)
					{
						Undo.RecordObject(sc, "Change Skybox Material");
						sc.skyboxCustomMaterial = newMat;
						sc.ApplySkybox();
					}
					break;
			}
		}

		// ---------------- Fog ----------------

		private void DrawFog()
		{
			bool newEnabled = EditorGUILayout.ToggleLeft(
				YanKLocalization.L("scFog", "Fog"), sc.fogEnabled);
			if (newEnabled != sc.fogEnabled)
			{
				Undo.RecordObject(sc, "Toggle Fog");
				sc.fogEnabled = newEnabled;
				RenderSettings.fog = newEnabled;
			}
			if (!sc.fogEnabled) return;

			var col = EditorGUILayout.ColorField(YanKLocalization.L("scFogColor", "Fog Color"), sc.fogColor);
			var mode = (FogMode)EditorGUILayout.EnumPopup(YanKLocalization.L("scFogMode", "Mode"), sc.fogMode);
			float dens = sc.fogDensity;
			float start = sc.fogLinearStart;
			float end = sc.fogLinearEnd;
			if (mode == FogMode.Linear)
			{
				start = EditorGUILayout.FloatField(YanKLocalization.L("scFogStart", "Start"), sc.fogLinearStart);
				end = EditorGUILayout.FloatField(YanKLocalization.L("scFogEnd", "End"), sc.fogLinearEnd);
			}
			else
			{
				dens = EditorGUILayout.Slider(YanKLocalization.L("scFogDensity", "Density"), sc.fogDensity, 0f, 1f);
			}

			if (col != sc.fogColor || mode != sc.fogMode ||
			    SceneControllerMath.Changed(dens, sc.fogDensity) ||
			    SceneControllerMath.Changed(start, sc.fogLinearStart) ||
			    SceneControllerMath.Changed(end, sc.fogLinearEnd))
			{
				Undo.RecordObject(sc, "Fog Settings");
				sc.fogColor = col;
				sc.fogMode = mode;
				sc.fogDensity = dens;
				sc.fogLinearStart = start;
				sc.fogLinearEnd = end;
			}
		}

		// ---------------- Debug Floor ----------------

		private void DrawDebugFloor()
		{
			bool newEnabled = EditorGUILayout.ToggleLeft(
				YanKLocalization.L("scDebugFloor", "Debug Floor (100m × 100m)"), sc.debugFloorEnabled);
			if (newEnabled != sc.debugFloorEnabled)
			{
				Undo.RecordObject(sc, "Toggle Debug Floor");
				sc.debugFloorEnabled = newEnabled;
				if (newEnabled) SceneControllerRig.EnsureDebugFloor(sc);
				else SceneControllerRig.DestroyDebugFloor(sc);
			}
			if (!sc.debugFloorEnabled) return;

			var baseCol = EditorGUILayout.ColorField(YanKLocalization.L("scFloorBase", "Base Color"), sc.debugFloorBaseColor);
			var lineCol = EditorGUILayout.ColorField(YanKLocalization.L("scFloorLine", "Line Color"), sc.debugFloorLineColor);
			if (baseCol != sc.debugFloorBaseColor || lineCol != sc.debugFloorLineColor)
			{
				Undo.RecordObject(sc, "Debug Floor Colors");
				sc.debugFloorBaseColor = baseCol;
				sc.debugFloorLineColor = lineCol;
				SceneControllerRig.ApplyDebugFloorColors(sc);
			}
		}

		// ---------------- Directional Light ----------------

		private void DrawDirectionalLight()
		{
			bool newEnabled = EditorGUILayout.ToggleLeft(
				YanKLocalization.L("scDirLight", "Directional Light"), sc.dirLightEnabled);
			if (newEnabled != sc.dirLightEnabled)
			{
				Undo.RecordObject(sc, "Toggle Directional Light");
				sc.dirLightEnabled = newEnabled;
				if (newEnabled) SceneControllerRig.EnsureDirectionalLight(sc);
				else SceneControllerRig.DestroyDirectionalLight(sc);
			}
			if (!sc.dirLightEnabled) return;

			// H / V sliders stacked; preset row BELOW them (per user request).
			EditorGUI.BeginChangeCheck();
			float h = EditorGUILayout.Slider(YanKLocalization.L("scHorizontal", "Horizontal"), sc.dirLightHorizontal, 0f, 360f);
			float v = EditorGUILayout.Slider(YanKLocalization.L("scVertical", "Vertical"), sc.dirLightVertical, -89f, 89f);
			if (EditorGUI.EndChangeCheck())
			{
				Undo.RecordObject(sc, "Directional Light HV");
				sc.dirLightHorizontal = h;
				sc.dirLightVertical = v;
			}

			DrawDirLightPresetRow();

			GUILayout.Space(4);

			if (_dirWorkflow.addOpen)
			{
				var act = SceneControllerPresetUI.DrawAddRow(ref _dirWorkflow.addName);
				if (act == SceneControllerPresetUI.AddAction.Save)
				{
					SaveDirPreset(_dirWorkflow.addName.Trim());
					_dirWorkflow.addOpen = false;
					GUIUtility.ExitGUI();
				}
				else if (act == SceneControllerPresetUI.AddAction.Cancel)
				{
					_dirWorkflow.addOpen = false;
				}
			}

			if (_dirWorkflow.removeOpen && !_dirWorkflow.addOpen) DrawDirRemovePanel();

			GUILayout.Space(4);

			// ---- Remaining light knobs ----
			Color c = EditorGUILayout.ColorField(YanKLocalization.L("scColor", "Color"), sc.dirLightColor);
			float intensity = EditorGUILayout.Slider(YanKLocalization.L("scIntensity", "Intensity"), sc.dirLightIntensity, 0f, 2f);
			var shadows = (LightShadows)EditorGUILayout.EnumPopup(YanKLocalization.L("scShadows", "Shadows"), sc.dirLightShadows);
			float ss = EditorGUILayout.Slider(YanKLocalization.L("scShadowStrength", "Shadow Strength"), sc.dirLightShadowStrength, 0f, 1f);

			if (c != sc.dirLightColor ||
			    SceneControllerMath.Changed(intensity, sc.dirLightIntensity) ||
			    shadows != sc.dirLightShadows ||
			    SceneControllerMath.Changed(ss, sc.dirLightShadowStrength))
			{
				Undo.RecordObject(sc, "Directional Light Settings");
				sc.dirLightColor = c;
				sc.dirLightIntensity = intensity;
				sc.dirLightShadows = shadows;
				sc.dirLightShadowStrength = ss;
			}
		}

		private void DrawDirLightPresetRow()
		{
			var presets = DirPresets;

			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField(
				YanKLocalization.L("scLightingPreset", "Preset"),
				GUILayout.Width(60));

			// Label reflects whichever preset currently matches the H/V sliders.
			string curDirLabel = YanKLocalization.L("scPickPreset", "— Pick preset —");
			foreach (var p in presets)
			{
				if (Mathf.Approximately(p.horizontal, sc.dirLightHorizontal) &&
				    Mathf.Approximately(p.vertical, sc.dirLightVertical))
				{
					curDirLabel = string.Format("{0}  (H {1:0}°, V {2:0}°)", p.name, p.horizontal, p.vertical);
					break;
				}
			}

			var items = new List<YanKSearchableDropdown.Item>(presets.Count);
			foreach (var p in presets)
			{
				items.Add(new YanKSearchableDropdown.Item
				{
					label = string.Format("{0}  (H {1:0}°, V {2:0}°)", p.name, p.horizontal, p.vertical),
					payload = p
				});
			}
			YanKSearchableDropdown.Field(
				curDirLabel,
				items,
				payload =>
				{
					var p = (SceneControllerPresets.DirLightPreset)payload;
					Undo.RecordObject(sc, "Apply Dir Light Preset");
					sc.dirLightHorizontal = p.horizontal;
					sc.dirLightVertical = p.vertical;
				},
				GUILayout.ExpandWidth(true));

			// Save button is LEFT of Remove per user spec.
			if (!_dirWorkflow.addOpen)
			{
				if (GUILayout.Button(YanKLocalization.L("scAddNewPreset", "Add New"),
					EditorStyles.miniButton, GUILayout.Width(70)))
				{
					_dirWorkflow.OpenAdd(SceneControllerPresets.NextDefaultDirLightPresetName("MyLight"));
				}
			}
			// Hide Remove button while saving.
			using (new EditorGUI.DisabledScope(_dirWorkflow.addOpen))
			{
				if (GUILayout.Button(YanKLocalization.L("scRemove", "Remove"),
					EditorStyles.miniButton, GUILayout.Width(70)))
				{
					_dirWorkflow.ToggleRemove();
				}
			}
			EditorGUILayout.EndHorizontal();
		}

		private void SaveDirPreset(string requested)
		{
			var user = SceneControllerPresets.LoadDirLightPresets();
			bool overwrite = user.Exists(p => p.name == requested);
			string finalName = overwrite ? requested : SceneControllerPresets.MakeUniqueDirLightPresetName(requested);

			SceneControllerPresets.AddDirLightPreset(new SceneControllerPresets.DirLightPreset
			{
				name = finalName,
				horizontal = sc.dirLightHorizontal,
				vertical = sc.dirLightVertical
			});
			ReloadDirPresets();
		}

		private void DrawDirRemovePanel()
		{
			var user = SceneControllerPresets.LoadDirLightPresets();
			var removable = new List<SceneControllerPresetUI.RemoveEntry<string>>(user.Count);
			foreach (var p in user)
			{
				if (p == null || string.IsNullOrEmpty(p.name)) continue;
				removable.Add(new SceneControllerPresetUI.RemoveEntry<string>(
					p.name,
					string.Format("{0}  (H {1:0}°, V {2:0}°)", p.name, p.horizontal, p.vertical)));
			}

			var action = SceneControllerPresetUI.DrawRemovePanel(
				removable, _dirWorkflow.removeSel, out var toRemove,
				YanKLocalization.L("scDirNoUserPresets", "No user presets. Built-ins cannot be removed."));

			if (action == SceneControllerPresetUI.RemoveAction.Confirm)
			{
				foreach (var n in toRemove)
					SceneControllerPresets.RemoveDirLightPreset(n);
				ReloadDirPresets();
				_dirWorkflow.CloseAll();
				GUIUtility.ExitGUI();
			}
			else if (action == SceneControllerPresetUI.RemoveAction.Cancel)
			{
				_dirWorkflow.CloseAll();
			}
		}

		// ---------------- Point Lights ----------------

		private void DrawPointLights()
		{
			bool newEnabled = EditorGUILayout.ToggleLeft(
				YanKLocalization.L("scPointLights", "Rotating Point Lights (5)"), sc.pointLightsEnabled);
			if (newEnabled != sc.pointLightsEnabled)
			{
				Undo.RecordObject(sc, "Toggle Point Lights");
				sc.pointLightsEnabled = newEnabled;
				if (newEnabled) SceneControllerRig.EnsurePointLights(sc);
				else SceneControllerRig.DestroyPointLights(sc);
			}

			if (!sc.pointLightsEnabled) return;

			bool changed = false;

			for (int i = 0; i < 5; i++)
			{
				EditorGUILayout.BeginHorizontal();
				EditorGUILayout.LabelField($"#{i + 1}", GUILayout.Width(28));
				var col = EditorGUILayout.ColorField(GUIContent.none, sc.pointLightColors[i], false, false, false, GUILayout.Width(60));
				float intensity = EditorGUILayout.Slider(sc.pointLightIntensities[i], 0f, 4f);
				if (col != sc.pointLightColors[i] || SceneControllerMath.Changed(intensity, sc.pointLightIntensities[i]))
				{
					sc.pointLightColors[i] = col;
					sc.pointLightIntensities[i] = intensity;
					changed = true;
				}
				EditorGUILayout.EndHorizontal();
			}

			GUILayout.Space(4);

			float r = EditorGUILayout.Slider(YanKLocalization.L("scPlRadius", "Radius"), sc.pointLightsRadius, 0.5f, 5f);
			float ph = EditorGUILayout.Slider(YanKLocalization.L("scPlHeight", "Height"), sc.pointLightsHeight, 0f, 3f);
			float rs = EditorGUILayout.Slider(YanKLocalization.L("scPlRotSpeed", "Rotation Speed"), sc.pointLightsRotationSpeed, -360f, 360f);
			var shadows = (LightShadows)EditorGUILayout.EnumPopup(YanKLocalization.L("scPlShadows", "Shadows (all)"), sc.pointLightsShadows);
			float ss = EditorGUILayout.Slider(YanKLocalization.L("scPlShadowStrength", "Shadow Strength (all)"), sc.pointLightsShadowStrength, 0f, 1f);

			if (SceneControllerMath.Changed(r, sc.pointLightsRadius)) { sc.pointLightsRadius = r; changed = true; }
			if (SceneControllerMath.Changed(ph, sc.pointLightsHeight)) { sc.pointLightsHeight = ph; changed = true; }
			if (SceneControllerMath.Changed(rs, sc.pointLightsRotationSpeed)) { sc.pointLightsRotationSpeed = rs; changed = true; }
			if (shadows != sc.pointLightsShadows) { sc.pointLightsShadows = shadows; changed = true; }
			if (SceneControllerMath.Changed(ss, sc.pointLightsShadowStrength)) { sc.pointLightsShadowStrength = ss; changed = true; }

			if (changed)
			{
				Undo.RecordObject(sc, "Point Lights Settings");
				SceneControllerRig.RefreshPointLightPositions(sc);
			}
		}

		// ---------------- Reflection Probe ----------------

		private List<Cubemap> _cubemaps;

		private void ReloadCubemaps()
		{
			_cubemaps = new List<Cubemap>();
			var guids = AssetDatabase.FindAssets(
				"t:Cubemap",
				new[] { "Packages/com.yank.avatartoolbox/Runtime/Cubemaps" });
			foreach (var g in guids)
			{
				var path = AssetDatabase.GUIDToAssetPath(g);
				var c = AssetDatabase.LoadAssetAtPath<Cubemap>(path);
				if (c != null) _cubemaps.Add(c);
			}
			_cubemaps.Sort((a, b) => string.Compare(a.name, b.name, System.StringComparison.OrdinalIgnoreCase));
		}

		private void DrawReflectionProbe()
		{
			bool newEnabled = EditorGUILayout.ToggleLeft(
				YanKLocalization.L("scReflProbe", "Reflection Probe"), sc.reflectionProbeEnabled);
			if (newEnabled != sc.reflectionProbeEnabled)
			{
				Undo.RecordObject(sc, "Toggle Reflection Probe");
				sc.reflectionProbeEnabled = newEnabled;
				if (newEnabled) SceneControllerRig.EnsureReflectionProbe(sc);
				else SceneControllerRig.DestroyReflectionProbe(sc);
			}
			if (!sc.reflectionProbeEnabled) return;

			// Auto-reload whenever the list is stale (mirrors how PP profiles work).
			if (_cubemaps == null) ReloadCubemaps();

			// Guard: cubemap asset was deleted while still serialised on the component.
			if (sc.reflectionProbeCubemap != null && !sc.reflectionProbeCubemap)
			{
				sc.reflectionProbeCubemap = null;
				SceneControllerRig.ApplyReflectionProbe(sc);
			}

			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField(
				YanKLocalization.L("scReflCubemap", "Cubemap"),
				GUILayout.Width(60));

			string currentLabel = sc.reflectionProbeCubemap != null
				? sc.reflectionProbeCubemap.name
				: YanKLocalization.L("scPickPreset", "\u2014 Pick preset \u2014");

			var items = new List<YanKSearchableDropdown.Item>();
			foreach (var c in _cubemaps)
				if (c != null) items.Add(new YanKSearchableDropdown.Item { label = c.name, payload = c });

			YanKSearchableDropdown.Field(currentLabel, items, payload =>
			{
				var cube = (Cubemap)payload;
				Undo.RecordObject(sc, "Change Reflection Cubemap");
				sc.reflectionProbeCubemap = cube;
				SceneControllerRig.ApplyReflectionProbe(sc);
			}, GUILayout.ExpandWidth(true));

			if (GUILayout.Button(YanKLocalization.L("scPing", "Ping"),
				EditorStyles.miniButton, GUILayout.Width(48)))
			{
				if (sc.reflectionProbe != null)
					EditorGUIUtility.PingObject(sc.reflectionProbe);
			}
			EditorGUILayout.EndHorizontal();

			if (_cubemaps.Count == 0)
			{
				EditorGUILayout.HelpBox(
					YanKLocalization.L("scReflNoCubes",
						"No Cubemaps found in Packages/com.yank.avatartoolbox/Runtime/Cubemaps. " +
						"Drop HDRIs in that folder and set their Texture Shape to Cube."),
					MessageType.Info);
			}

			float newIntensity = EditorGUILayout.Slider(
				YanKLocalization.L("scReflIntensity", "Intensity"), sc.reflectionProbeIntensity, 0f, 4f);
			if (!Mathf.Approximately(newIntensity, sc.reflectionProbeIntensity))
			{
				Undo.RecordObject(sc, "Reflection Intensity");
				sc.reflectionProbeIntensity = newIntensity;
				SceneControllerRig.ApplyReflectionProbe(sc);
			}
		}
	}
}
