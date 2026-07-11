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
			var toConvert = conversionSlots.Where(s => s.selected && s.material != null).ToList();
			if (toConvert.Count == 0)
			{
				EditorUtility.DisplayDialog(
					L("ncConvertDoneTitle", "Conversion Complete"),
					L("ncNoSelection",      "Select at least one material to convert."), "OK");
				return;
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

			// Pre-resolve mask overflow BEFORE StartAssetEditing (needs dialogs)
			var maskChoices = options.BakeMasks
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
			EditorUtility.DisplayDialog(
				L("ncConvertDoneTitle", "Conversion Complete"),
				string.Format(L("ncConvertDone", "Converted {0} material(s). {1} failed."), succeeded, failed), "OK");

			ScanMaterials();
			Repaint();
		}

		// =====================================================================
		// Single-material conversion
		// =====================================================================

		private Material ConvertMaterial(ConversionSlot slot, ConverterOptions opt, List<MaskCandidate> resolvedMasks)
		{
			var src = slot.material;
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
			if (isFur) ApplyMappings(src, dst, FurMappings, isFurOnly: true);

			// 3. RimLight (gated on _UseRim; HDR→LDR + alpha→darkness)
			if (IsFeatureActive(src, "_UseRim") &&
			    src.HasProperty("_RimColor") && dst.HasProperty(P_RimLight + "RimLightColor"))
			{
				dst.SetColor(P_RimLight + "RimLightColor", HdrToLdr(src.GetColor("_RimColor"), applyAlpha: true));
			}

			// 4. MatCaps (gated on _UseMatCap / _UseMatCap2nd; enable module + LDR-clamped colors)
			bool useMatCap    = IsFeatureActive(src, "_UseMatCap");
			bool useMatCap2nd = IsFeatureActive(src, "_UseMatCap2nd");
			if ((useMatCap || useMatCap2nd) && dst.HasProperty(P_MatCaps + "Enable"))
			{
				SetModuleEnable(dst, P_MatCaps + "Enable", true);
				if (useMatCap && src.HasProperty("_MatCapColor") && dst.HasProperty(P_MatCaps + "MatCapMultiplyColor"))
					dst.SetColor(P_MatCaps + "MatCapMultiplyColor", HdrToLdr(src.GetColor("_MatCapColor"), applyAlpha: false));
				if (useMatCap2nd && src.HasProperty("_MatCap2ndColor") && dst.HasProperty(P_MatCaps + "MatCapAddColor"))
					dst.SetColor(P_MatCaps + "MatCapAddColor", HdrToLdr(src.GetColor("_MatCap2ndColor"), applyAlpha: false));
			}

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
				return customOutputFolder;
			if (!string.IsNullOrEmpty(srcAssetPath))
				return Path.GetDirectoryName(srcAssetPath).Replace('\\', '/');
			return "Assets";
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
