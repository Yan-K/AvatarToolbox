using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace YanK
{
	public partial class NonToonConverterTool
	{
		private const string NonToonShaderName    = "NonToon";
		private const string NonToonFurShaderName = "NonToonFur";

		// =====================================================================
		// Entry point
		// =====================================================================

		private void ConvertSelected()
		{
			var selected = conversionSlots.Where(s => s.selected && s.material != null).ToList();
			if (selected.Count == 0)
			{
				EditorUtility.DisplayDialog(
					L("ncConvertDoneTitle", "Conversion Complete"),
					L("ncNoSelection",      "Select at least one material to convert."), "OK");
				return;
			}

			// "Convert Fur" OFF means Fur materials are left completely untouched (stay lilToon) —
			// filter them out of this batch entirely rather than converting them without fur data.
			var toConvert  = selected;
			int skippedFur = 0;
			if (!options.ConvertFur)
			{
				toConvert = new List<ConversionSlot>();
				foreach (var s in selected)
				{
					if (IsFurMaterial(s.material)) skippedFur++;
					else toConvert.Add(s);
				}

				if (toConvert.Count == 0)
				{
					EditorUtility.DisplayDialog(
						L("ncConvertDoneTitle", "Conversion Complete"),
						L("ncAllSelectedAreFur", "All selected materials are Fur materials, and Convert Fur is off in Advanced Settings — nothing to convert."),
						"OK");
					return;
				}
			}

			if (!EditorUtility.DisplayDialog(
				L("ncConfirmConvertTitle", "Confirm Conversion"),
				string.Format(L("ncConfirmConvert", "Convert {0} material(s)?"), toConvert.Count),
				"OK", "Cancel"))
				return;

			if (Shader.Find(NonToonShaderName) == null)
			{
				EditorUtility.DisplayDialog("Error",
					L("ncNoShader", "NonToon shader not found. Ensure jp.lilxyzw.nontoon package is installed."), "OK");
				return;
			}

			// Create a missing custom Assets folder through AssetDatabase so every folder gets
			// its GUID/.meta synchronously before material and editable Shader Core assets are made.
			if (!EnsureCustomOutputFolder()) return;

			// Pre-flight: offer to remove any lilToon Fake Shadow slots (they cause artifacts)
			if (fakeShadowSlots.Count > 0)
			{
				var msg = new System.Text.StringBuilder();
				msg.AppendLine($"Found {fakeShadowSlots.Count} Fake Shadow material slot(s) that will cause rendering artifacts:\n");
				foreach (var s in fakeShadowSlots)
					msg.AppendLine($"  {s.ObjectName}  (slot {s.materialIndex})");
				msg.AppendLine("\nRemove only those slots from the renderers?");
				if (EditorUtility.DisplayDialog("Fake Shadow Detected", msg.ToString(), "Remove Slots", "Skip"))
					RemoveFakeShadowSlots();
			}

			// Pre-resolve mask overflow BEFORE StartAssetEditing (needs dialogs)
			var maskChoices = options.BakeMasks || options.BakeEmission
				? PreResolveMaskOverflow(toConvert, options)
				: new Dictionary<Material, List<MaskCandidate>>();

			int succeeded = 0, failed = 0;
			Undo.SetCurrentGroupName("YNC Convert Materials");
			int undoGroup = Undo.GetCurrentGroup();

			// NOTE: do NOT wrap in AssetDatabase.StartAssetEditing() — baking creates texture
			// assets and immediately loads them back; batched asset editing defers imports,
			// which would make every baked texture load as null.
			try
			{
				foreach (var slot in toConvert)
				{
					try
					{
						var resolved = maskChoices.TryGetValue(slot.material, out var mc) ? mc : null;
						var converted = ConvertMaterial(slot, options, resolved);
						if (converted == null) { failed++; continue; }
						// Auto-assign only when material came from a renderer
						if (!slot.MaterialOnly)
							AssignToRenderers(slot, converted);
						succeeded++;
					}
					catch (System.Exception ex)
					{
						Debug.LogError($"[YNC] Failed to convert {slot.material.name}: {ex}");
						failed++;
					}
				}
			}
			finally
			{
				AssetDatabase.SaveAssets();
				AssetDatabase.Refresh();
			}

			Undo.CollapseUndoOperations(undoGroup);

			string doneMessage = string.Format(L("ncConvertDone", "Converted {0} material(s). {1} failed."), succeeded, failed);
			if (skippedFur > 0)
				doneMessage += "\n" + string.Format(L("ncConvertSkippedFur", "Skipped Fur materials: {0} (Convert Fur is off; they remain lilToon)."), skippedFur);
			EditorUtility.DisplayDialog(L("ncConvertDoneTitle", "Conversion Complete"), doneMessage, "OK");

			if (failed == 0)
			{
				// Everything converted cleanly — clear the drop area so it's ready for the next batch.
				roots.Clear();
				conversionSlots.Clear();
				fakeShadowSlots.Clear();
				selectAll = false;
			}
			else
			{
				// Keep the roots so the user can inspect/retry the failed material(s).
				ScanMaterials();
			}
			Repaint();
		}

		// =====================================================================
		// Single-material conversion
		// =====================================================================

		private Material ConvertMaterial(ConversionSlot slot, ConverterOptions opt, List<MaskCandidate> resolvedMasks)
		{
			var src = slot.material;
			// NOTE: ConvertSelected() already filters Fur materials out of the batch entirely when
			// opt.ConvertFur is off (they must stay untouched lilToon), so by the time we get here
			// it's always safe to detect Fur from the shader alone.
			bool   isFur        = IsFurMaterial(src);
			string targetName   = isFur ? NonToonFurShaderName : NonToonShaderName;
			var    targetShader = Shader.Find(targetName);

			if (targetShader == null)
			{
				Debug.LogWarning($"[YNC] Shader '{targetName}' not found — skipping {src.name}");
				return null;
			}

			// Determine output path
			string srcPath  = AssetDatabase.GetAssetPath(src);
			string folder   = ResolveOutputFolder(srcPath);
			string baseName = string.IsNullOrEmpty(srcPath) ? src.name : Path.GetFileNameWithoutExtension(srcPath);
			string dstPath  = AssetDatabase.GenerateUniqueAssetPath(
			                      Path.Combine(folder, baseName + "_NT.mat").Replace('\\', '/'));

			var dst = new Material(targetShader) { name = Path.GetFileNameWithoutExtension(dstPath) };

			// 1. Rendering mode
			int renderMode = DetectRenderingMode(src, opt.ForceRenderingMode);
			ApplyRenderingMode(dst, renderMode, opt);

			// 2. Core visual mappings (prefixed module names)
			ApplyMappings(src, dst, CoreMappings, isFurOnly: false);
			if (isFur)
			{
				ApplyMappings(src, dst, FurMappings, isFurOnly: true);
				ApplyFurVector(src, dst);
			}

			// 3. RimLight (gated on _UseRim; HDR→LDR + alpha→darkness)
			if (IsFeatureActive(src, "_UseRim") &&
			    src.HasProperty("_RimColor") && dst.HasProperty(P_RimLight + "RimLightColor"))
			{
				dst.SetColor(P_RimLight + "RimLightColor", HdrToLdr(src.GetColor("_RimColor"), applyAlpha: true));
			}

			// 4. MatCaps — blend-mode-aware routing + blur bake
			ApplyMatCaps(src, dst, folder, baseName);

			// 5. Outline — port ONLY when the source uses an outline SHADER VARIANT
			//    (Hidden/lilToonOutline, Hidden/lilToonCutoutOutline, Hidden/lilToonTransparentOutline,
			//    …) or lilToon Multi with _UseOutline. NEVER infer from _OutlineWidth — lilToon keeps
			//    a non-zero width stored even when outline is disabled.
			if (HasOutlineEnabled(src))
			{
				CopyColorIfExists(src, dst, "_OutlineColor", "_OutlineColor");
				CopyFloatIfExists(src, dst, "_OutlineWidth", "_OutlineWidth");
				// lilToon's outline Z-bias 0 looks closest to NonToon's 0.001 — apply it whenever
				// we port an outline width.
				SetFloatIfExists(dst, "_OutlineZOffset", 0.001f);
			}
			else
			{
				// IMPORTANT: NonToon ships a NON-ZERO default outline (_OutlineWidth = 0.1), so a
				// freshly created material renders an outline even when the source had none. Zero it
				// so no phantom outline appears on non-outline materials.
				SetFloatIfExists(dst, "_OutlineWidth", 0f);
			}

			// 6. Advanced pipeline copies
			if (opt.CopyCull)    CopyIntOrFloat(src, dst, "_Cull");
			if (opt.CopyZWrite)  CopyIntOrFloat(src, dst, "_ZWrite");
			if (opt.CopyBlend)
			{
				CopyIntOrFloat(src, dst, "_SrcBlend");
				CopyIntOrFloat(src, dst, "_DstBlend");
				CopyIntOrFloat(src, dst, "_SrcBlendAlpha");
				CopyIntOrFloat(src, dst, "_DstBlendAlpha");
				CopyIntOrFloat(src, dst, "_AlphaToMask");
			}
			if (opt.CopyStencil)
			{
				CopyIntOrFloat(src, dst, "_StencilRef");
				CopyIntOrFloat(src, dst, "_StencilComp");
				CopyIntOrFloat(src, dst, "_StencilPass");
				CopyIntOrFloat(src, dst, "_OutlineStencilRef");
				CopyIntOrFloat(src, dst, "_OutlineStencilComp");
				CopyIntOrFloat(src, dst, "_OutlineStencilPass");
			}

			// 7. Save asset first (baking writes additional assets into same folder)
			AssetDatabase.CreateAsset(dst, dstPath);
			string persistedPath = AssetDatabase.GetAssetPath(dst).Replace('\\', '/');
			string materialGuid = AssetDatabase.AssetPathToGUID(dstPath, AssetPathToGUIDOptions.OnlyExistingAssets);
			if (!persistedPath.Equals(dstPath, System.StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(materialGuid))
				throw new IOException($"Unity did not create a persistent material asset at '{dstPath}'.");
			Undo.RegisterCreatedObjectUndo(dst, "Create NonToon Material");

			// Override render queue after save if requested
			if (opt.CopyRenderQueue)
				SetMaterialRenderQueue(dst, src.renderQueue);

			// 8. Baking (modifies dst properties after the asset exists)
			RunBakes(src, dst, opt, folder, baseName, resolvedMasks);

			EditorUtility.SetDirty(dst);
			return dst;
		}

		// =====================================================================
		// Renderer assignment
		// =====================================================================

		private static void AssignToRenderers(ConversionSlot slot, Material dst)
		{
			foreach (var group in slot.refs.GroupBy(r => r.renderer))
			{
				var renderer = group.Key;
				if (renderer == null) continue;
				Undo.RecordObject(renderer, "YNC Assign Converted Material");
				var mats = renderer.sharedMaterials;
				foreach (var r in group)
					if (r.materialIndex < mats.Length) mats[r.materialIndex] = dst;
				renderer.sharedMaterials = mats;
				EditorUtility.SetDirty(renderer);
			}
		}

		// =====================================================================
		// Rendering mode (mirrors NTRenderingModeElement)
		// =====================================================================

		private static void ApplyRenderingMode(Material dst, int mode, ConverterOptions opt)
		{
			// _RenderingMode is SC_uint → Integer type
			SetIntIfExists(dst, "_RenderingMode", mode);

			if (!opt.CopyBlend)
			{
				switch (mode)
				{
					case 0: // Opaque
						SetIntIfExists(dst, "_SrcBlend",    (int)UnityEngine.Rendering.BlendMode.One);
						SetIntIfExists(dst, "_DstBlend",    (int)UnityEngine.Rendering.BlendMode.Zero);
						SetIntIfExists(dst, "_AlphaToMask", 0);
						break;
					case 1: // Cutout
						SetIntIfExists(dst, "_SrcBlend",    (int)UnityEngine.Rendering.BlendMode.One);
						SetIntIfExists(dst, "_DstBlend",    (int)UnityEngine.Rendering.BlendMode.Zero);
						SetIntIfExists(dst, "_AlphaToMask", 1);
						break;
					case 2: // Transparent
						SetIntIfExists(dst, "_SrcBlend",    (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
						SetIntIfExists(dst, "_DstBlend",    (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
						SetIntIfExists(dst, "_AlphaToMask", 0);
						break;
				}
			}

			if (!opt.CopyRenderQueue)
			{
				int queue = mode == 0 ? -1 : mode == 1 ? 2450 : 3000;
				SetMaterialRenderQueue(dst, queue);
			}
		}

		private static void SetMaterialRenderQueue(Material m, int queue)
		{
			using (var so = new SerializedObject(m))
			{
				var p = so.FindProperty("m_CustomRenderQueue");
				if (p != null) { p.intValue = queue; so.ApplyModifiedPropertiesWithoutUndo(); }
			}
		}

		// =====================================================================
		// Prop-mapping applier
		// =====================================================================

		private static void ApplyMappings(Material src, Material dst, PropMapping[] mappings, bool isFurOnly)
		{
			foreach (var m in mappings)
			{
				if (m.furOnly != isFurOnly) continue;
				if (m.enableCheck != null && !m.enableCheck(src)) continue;
				if (!src.HasProperty(m.sourceProp) || !dst.HasProperty(m.targetProp)) continue;

				var srcType = GetPropType(src, m.sourceProp);

				switch (m.kind)
				{
					case PropKind.Texture:
						if (srcType != UnityEngine.Rendering.ShaderPropertyType.Texture) break;
						var tex = src.GetTexture(m.sourceProp);
						if (tex != null) dst.SetTexture(m.targetProp, tex);
						break;
					case PropKind.Color:
						if (srcType != UnityEngine.Rendering.ShaderPropertyType.Color &&
						    srcType != UnityEngine.Rendering.ShaderPropertyType.Vector) break;
						dst.SetColor(m.targetProp, src.GetColor(m.sourceProp));
						break;
					case PropKind.Float:
						if (srcType != UnityEngine.Rendering.ShaderPropertyType.Float &&
						    srcType != UnityEngine.Rendering.ShaderPropertyType.Range &&
						    srcType != UnityEngine.Rendering.ShaderPropertyType.Int) break;
						float fv = src.GetFloat(m.sourceProp);
						if (m.floatTransform != null) fv = m.floatTransform(fv);
						// Destination may be a true Integer shader property (e.g. NonToon's
						// _FurSubdivision is SC_uint) — SetFloat on those throws "already exists
						// with a different type", so branch the same way SetIntIfExists does.
						if (GetPropType(dst, m.targetProp) == UnityEngine.Rendering.ShaderPropertyType.Int)
							dst.SetInteger(m.targetProp, Mathf.RoundToInt(fv));
						else
							dst.SetFloat(m.targetProp, fv);
						break;
					case PropKind.Vector:
						if (srcType != UnityEngine.Rendering.ShaderPropertyType.Vector &&
						    srcType != UnityEngine.Rendering.ShaderPropertyType.Color) break;
						dst.SetVector(m.targetProp, src.GetVector(m.sourceProp));
						break;
				}
			}
		}

		/// <summary>Returns the shader property type, or Float if the property is not found.</summary>
		private static UnityEngine.Rendering.ShaderPropertyType GetPropType(Material m, string prop)
		{
			int idx = m.shader.FindPropertyIndex(prop);
			return idx >= 0 ? m.shader.GetPropertyType(idx) : UnityEngine.Rendering.ShaderPropertyType.Float;
		}

		// =====================================================================
		// Output folder resolution
		// =====================================================================

		private string ResolveOutputFolder(string srcAssetPath)
		{
			if (outputMode == OutputMode.Custom && !string.IsNullOrEmpty(customOutputFolder))
				return NormalizeAssetFolder(customOutputFolder);
			if (!string.IsNullOrEmpty(srcAssetPath))
				return Path.GetDirectoryName(srcAssetPath).Replace('\\', '/');
			return "Assets";
		}

		private bool EnsureCustomOutputFolder()
		{
			if (outputMode != OutputMode.Custom) return true;
			if (string.IsNullOrWhiteSpace(customOutputFolder))
			{
				EditorUtility.DisplayDialog(
					L("ncOutputFolderErrorTitle", "Invalid Output Folder"),
					L("ncOutputFolderInvalidPath", "Enter a custom output folder inside Assets."),
					"OK");
				return false;
			}

			string folder = NormalizeAssetFolder(customOutputFolder);
			if (string.IsNullOrEmpty(folder) ||
			    !(folder.Equals("Assets", System.StringComparison.OrdinalIgnoreCase) ||
			      folder.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase)))
			{
				EditorUtility.DisplayDialog(
					L("ncOutputFolderErrorTitle", "Invalid Output Folder"),
					L("ncOutputFolderMustBeAssets", "The custom output folder must be inside this project's Assets folder."),
					"OK");
				return false;
			}
			if (folder.Equals("Assets/StreamingAssets", System.StringComparison.OrdinalIgnoreCase) ||
			    folder.StartsWith("Assets/StreamingAssets/", System.StringComparison.OrdinalIgnoreCase))
			{
				EditorUtility.DisplayDialog(
					L("ncOutputFolderErrorTitle", "Invalid Output Folder"),
					L("ncOutputFolderStreamingAssets", "Unity cannot create material assets inside StreamingAssets. Choose another Assets folder."),
					"OK");
				return false;
			}

			var segments = folder.Split('/');
			if (segments.Any(s => s == "." || s == ".." || string.IsNullOrWhiteSpace(s)))
			{
				EditorUtility.DisplayDialog(
					L("ncOutputFolderErrorTitle", "Invalid Output Folder"),
					L("ncOutputFolderInvalidPath", "The custom output folder contains an invalid path segment."),
					"OK");
				return false;
			}

			string current = "Assets";
			for (int i = 1; i < segments.Length; i++)
			{
				string next = current + "/" + segments[i];
				if (!AssetDatabase.IsValidFolder(next) && Directory.Exists(next))
					AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

				if (!AssetDatabase.IsValidFolder(next))
				{
					if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(next, AssetPathToGUIDOptions.OnlyExistingAssets)))
					{
						EditorUtility.DisplayDialog(
							L("ncOutputFolderErrorTitle", "Invalid Output Folder"),
							string.Format(L("ncOutputFolderFileCollision", "A file already occupies part of the requested output path:\n{0}"), next),
							"OK");
						return false;
					}

					string guid = AssetDatabase.CreateFolder(current, segments[i]);
					string createdPath = string.IsNullOrEmpty(guid) ? "" : AssetDatabase.GUIDToAssetPath(guid);
					if (string.IsNullOrEmpty(guid) || !createdPath.Equals(next, System.StringComparison.OrdinalIgnoreCase))
					{
						EditorUtility.DisplayDialog(
							L("ncOutputFolderErrorTitle", "Invalid Output Folder"),
							string.Format(L("ncOutputFolderCreateFailed", "Could not create output folder:\n{0}"), next),
							"OK");
						return false;
					}
				}

				current = next;
			}

			if (!AssetDatabase.IsValidFolder(folder))
			{
				EditorUtility.DisplayDialog(
					L("ncOutputFolderErrorTitle", "Invalid Output Folder"),
					string.Format(L("ncOutputFolderCreateFailed", "Could not create output folder:\n{0}"), folder),
					"OK");
				return false;
			}

			if (customOutputFolder != folder) SetCustomOutputFolder(folder);
			return true;
		}

		private static string NormalizeAssetFolder(string folder)
		{
			if (string.IsNullOrWhiteSpace(folder)) return "";

			string normalized = folder.Trim().Replace('\\', '/').TrimEnd('/');
			while (normalized.Contains("//")) normalized = normalized.Replace("//", "/");

			string assetsFullPath = Application.dataPath.Replace('\\', '/').TrimEnd('/');
			if (normalized.Equals(assetsFullPath, System.StringComparison.OrdinalIgnoreCase))
				return "Assets";
			if (normalized.StartsWith(assetsFullPath + "/", System.StringComparison.OrdinalIgnoreCase))
				normalized = "Assets" + normalized.Substring(assetsFullPath.Length);

			if (normalized.Equals("Assets", System.StringComparison.OrdinalIgnoreCase)) return "Assets";
			if (normalized.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase))
				return "Assets/" + normalized.Substring("Assets/".Length);
			return normalized;
		}

		// =====================================================================
		// MatCap — blend-mode routing + blur bake
		// =====================================================================

		/// <summary>
		/// Decides how lilToon's two matcap slots map onto NonToon's single Multiply slot and single
		/// Add slot:
		///   3 (Multiply)                      → Multiply slot
		///   0 (Normal) / 1 (Add) / 2 (Screen)  → Add slot
		/// NonToon has ONLY one Multiply and one Add slot, so if both lilToon matcaps request the SAME
		/// slot, the 2nd matcap is DROPPED entirely (not remapped to the other slot — silently changing
		/// its blend mode would be wrong). Shared by mask-candidate collection (Bake.cs) and value
		/// assignment (below) so both agree on which matcap uses which slot / whether it was dropped.
		/// </summary>
		private static void DecideMatCapRouting(Material src,
		                                         out bool use1, out bool mul1,
		                                         out bool use2, out bool mul2,
		                                         out bool drop2)
		{
			use1 = IsFeatureActive(src, "_UseMatCap");
			use2 = IsFeatureActive(src, "_UseMatCap2nd");

			int mode1 = (use1 && src.HasProperty("_MatCapBlendMode"))    ? (int)src.GetFloat("_MatCapBlendMode")    : 1;
			int mode2 = (use2 && src.HasProperty("_MatCap2ndBlendMode")) ? (int)src.GetFloat("_MatCap2ndBlendMode") : 1;
			mul1 = mode1 == 3; // Multiply only — Normal/Add/Screen all route to the Add slot
			mul2 = mode2 == 3;

			drop2 = false;
			if (use1 && use2 && mul1 == mul2)
			{
				// Both matcaps want the same NonToon slot — NonToon can't host 2× Multiply or 2× Add,
				// so drop the 2nd rather than silently reassigning its blend mode.
				drop2 = true;
				use2  = false;
			}
		}

		/// <summary>
		/// Applies matcap colors/textures using <see cref="DecideMatCapRouting"/>. If the source
		/// material has a Blur (Lod) value the texture is GPU-blurred before assigning.
		/// </summary>
		private static void ApplyMatCaps(Material src, Material dst, string folder, string baseName)
		{
			DecideMatCapRouting(src, out bool use1, out bool mul1, out bool use2, out bool mul2, out bool drop2);
			if (!use1 && !use2) return;
			if (!dst.HasProperty(P_MatCaps + "Enable")) return;

			SetModuleEnable(dst, P_MatCaps + "Enable", true);

			if (drop2)
			{
				Debug.Log($"[YNC] {src.name}: 2nd MatCap uses the same blend mode as the 1st — " +
				          "dropped (NonToon only supports one Multiply and one Add MatCap slot).");
			}

			if (use1)
			{
				string cProp = P_MatCaps + (mul1 ? "MatCapMultiplyColor" : "MatCapAddColor");
				string tProp = P_MatCaps + (mul1 ? "MatCapMultiply"      : "MatCapAdd");
				float  s1    = MatCapStrength(src, "_MatCapColor", "_MatCapBlend");
				if (src.HasProperty("_MatCapColor") && dst.HasProperty(cProp))
				{
					Color c = HdrToLdr(src.GetColor("_MatCapColor"), applyAlpha: false);
					// Add slot is linear in color → fold strength into RGB. Multiply slot keeps the
					// tint; strength is baked into the texture (fade toward white) instead.
					if (!mul1) { c.r *= s1; c.g *= s1; c.b *= s1; }
					dst.SetColor(cProp, c);
				}
				AssignMatCapTex(src, "_MatCapTex",    "_MatCapLod",    dst, tProp, mul1, s1, folder, baseName + "_MatCap1");
			}
			if (use2)
			{
				string cProp = P_MatCaps + (mul2 ? "MatCapMultiplyColor" : "MatCapAddColor");
				string tProp = P_MatCaps + (mul2 ? "MatCapMultiply"      : "MatCapAdd");
				float  s2    = MatCapStrength(src, "_MatCap2ndColor", "_MatCap2ndBlend");
				if (src.HasProperty("_MatCap2ndColor") && dst.HasProperty(cProp))
				{
					Color c = HdrToLdr(src.GetColor("_MatCap2ndColor"), applyAlpha: false);
					if (!mul2) { c.r *= s2; c.g *= s2; c.b *= s2; }
					dst.SetColor(cProp, c);
				}
				AssignMatCapTex(src, "_MatCap2ndTex", "_MatCap2ndLod", dst, tProp, mul2, s2, folder, baseName + "_MatCap2");
			}
		}

		/// <summary>
		/// lilToon blends a matcap with strength <c>_MatCapBlend * _MatCapColor.a * mask</c>
		/// (lil_common_frag.hlsl). NonToon's matcap has no blend/alpha term — the color alpha is
		/// ignored — so the per-material scalar (blend × color.a) is lost, leaving the matcap at full
		/// strength (glossy skin). We recover that scalar here; the caller folds it into the Add color
		/// or the Multiply texture. The per-pixel mask term is handled separately by SharedMask packing.
		/// </summary>
		private static float MatCapStrength(Material src, string colorProp, string blendProp)
		{
			float blend = src.HasProperty(blendProp) ? src.GetFloat(blendProp) : 1f;
			float alpha = src.HasProperty(colorProp) ? src.GetColor(colorProp).a : 1f;
			return Mathf.Clamp01(blend) * Mathf.Clamp01(alpha);
		}

		private static void AssignMatCapTex(Material src, string srcTexProp, string lodProp,
		                                     Material dst, string dstTexProp, bool multiply, float strength,
		                                     string folder, string nameNoExt)
		{
			if (!src.HasProperty(srcTexProp) || !dst.HasProperty(dstTexProp)) return;
			var srcTex = src.GetTexture(srcTexProp) as Texture2D;
			if (srcTex == null) return;

			float lod = src.HasProperty(lodProp) ? src.GetFloat(lodProp) : 0f;
			// Multiply matcaps carry their strength in the texture (fade toward white); Add matcaps
			// already have it folded into the color, so pass strength = 1 to skip the fade.
			float texStrength = multiply ? strength : 1f;

			bool needBlur = lod > 0.05f;
			bool needFade = texStrength < 0.996f;
			var  result   = (needBlur || needFade)
			                ? BakeMatCap(srcTex, needBlur ? lod : 0f, texStrength, folder, nameNoExt)
			                : srcTex;

			if (result != null) dst.SetTexture(dstTexProp, result);
		}

		// =====================================================================
		// Fur vector — direction + length combine differently between shaders
		// =====================================================================

		/// <summary>
		/// lilToon's <c>_FurVector</c> is (direction.xyz, length.w) — the shader normalizes xyz
		/// then multiplies by w to get the final tangent-space offset. NonToon's <c>_FurVector</c>
		/// has NO separate length: its raw xyz magnitude IS the fur length (no per-shader
		/// normalize step). A straight copy would keep lilToon's unit direction and drop the
		/// length entirely, so bake direction×length into xyz here instead (w stays 0, matching
		/// NonToon's own default and its [SCVector3] editor drawer which hides w).
		/// </summary>
		private static void ApplyFurVector(Material src, Material dst)
		{
			const string prop = "_FurVector";
			if (!src.HasProperty(prop) || !dst.HasProperty(prop)) return;

			Vector4 v = src.GetVector(prop);
			// lilToon adds a small Z epsilon before normalizing so an all-zero direction still
			// resolves to a sane default (pointing along the normal) instead of NaN.
			Vector3 dir    = (new Vector3(v.x, v.y, v.z) + new Vector3(0f, 0f, 0.001f)).normalized;
			Vector3 result = dir * v.w;

			// Bake in _FurGravity — NonToon has no separate gravity property at all, so lilToon's
			// per-vertex world-space bend ("furVector.y -= _FurGravity * length(furVector)", applied
			// AFTER the object→world transform in lil_common_vert_fur.hlsl) has to be folded into this
			// static vector instead. Best-effort approximation: real lilToon gravity depends on each
			// vertex's world-space orientation, which isn't available at material-conversion time, so
			// we apply the same subtraction directly to the un-transformed vector.
			if (src.HasProperty("_FurGravity"))
			{
				float gravity = src.GetFloat("_FurGravity");
				if (gravity != 0f)
					result.y -= gravity * result.magnitude;
			}

			dst.SetVector(prop, new Vector4(result.x, result.y, result.z, 0f));
		}

		// =====================================================================
		// Low-level prop helpers
		// =====================================================================

		/// <summary>
		/// Set a numeric value on a property that may be Integer- or Float-typed.
		/// NonToon SC_uint/SC_int props (e.g. _RenderingMode, mask channels, _Enable) are true
		/// Integer, while ShaderLab-declared Int props (e.g. _SrcBlend, _Cull, _ZWrite, stencil,
		/// _AlphaToMask) are Float-backed. Calling the wrong setter throws
		/// "Property already exists with a different type".
		/// </summary>
		private static void SetIntIfExists(Material m, string prop, int value)
		{
			if (!m.HasProperty(prop)) return;
			int idx = m.shader.FindPropertyIndex(prop);
			if (idx >= 0 && m.shader.GetPropertyType(idx) == UnityEngine.Rendering.ShaderPropertyType.Int)
				m.SetInteger(prop, value);
			else
				m.SetFloat(prop, value);
		}

		/// <summary>
		/// Enable/disable a NonToon module that uses an `_Enable` toggle. This requires BOTH the
		/// integer value AND the generated shader keyword (`{PROP}_1` on / `{PROP}_0` off), because
		/// the shader gates the feature on the keyword, not the property value.
		/// </summary>
		private static void SetModuleEnable(Material dst, string enableProp, bool on)
		{
			if (!dst.HasProperty(enableProp)) return;
			SetIntIfExists(dst, enableProp, on ? 1 : 0);
			string kw = enableProp.ToUpperInvariant();
			if (on) { dst.EnableKeyword(kw + "_1");  dst.DisableKeyword(kw + "_0"); }
			else    { dst.EnableKeyword(kw + "_0");  dst.DisableKeyword(kw + "_1"); }
		}

		/// <summary>
		/// Convert a lilToon HDR color to an LDR color NonToon can display. If a channel exceeds 1,
		/// the color is normalized by its max channel (preserving hue). When <paramref name="applyAlpha"/>
		/// is true (rim), the alpha is folded into brightness (alpha→darkness). Alpha is returned as 1.
		/// </summary>
		private static Color HdrToLdr(Color c, bool applyAlpha)
		{
			float maxC = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
			if (maxC > 1f) { c.r /= maxC; c.g /= maxC; c.b /= maxC; }
			if (applyAlpha)
			{
				float a = Mathf.Clamp01(c.a);
				c.r *= a; c.g *= a; c.b *= a;
			}
			return new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f);
		}

		/// <summary>Set float on a Float-typed shader property.</summary>
		private static void SetFloatIfExists(Material m, string prop, float value)
		{
			if (m.HasProperty(prop)) m.SetFloat(prop, value);
		}

		/// <summary>Copy a value that might be Integer or Float on the destination shader.</summary>
		private static void CopyIntOrFloat(Material src, Material dst, string prop)
		{
			if (!src.HasProperty(prop) || !dst.HasProperty(prop)) return;
			// Use shader property index to determine type on dst
			var shader   = dst.shader;
			int propIdx  = shader.FindPropertyIndex(prop);
			if (propIdx < 0) return;
			var propType = shader.GetPropertyType(propIdx);
			float val    = src.GetFloat(prop);
			if (propType == UnityEngine.Rendering.ShaderPropertyType.Int)
				dst.SetInteger(prop, (int)val);
			else
				dst.SetFloat(prop, val);
		}

		private static void CopyFloatIfExists(Material src, Material dst, string srcProp, string dstProp)
		{
			if (src.HasProperty(srcProp) && dst.HasProperty(dstProp))
				dst.SetFloat(dstProp, src.GetFloat(srcProp));
		}

		private static void CopyColorIfExists(Material src, Material dst, string srcProp, string dstProp)
		{
			if (src.HasProperty(srcProp) && dst.HasProperty(dstProp))
				dst.SetColor(dstProp, src.GetColor(srcProp));
		}
	}
}
