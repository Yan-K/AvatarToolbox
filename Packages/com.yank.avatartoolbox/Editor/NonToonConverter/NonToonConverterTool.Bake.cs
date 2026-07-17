using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace YanK
{
	public partial class NonToonConverterTool
	{
		// =====================================================================
		// Mask candidate (pre-resolved before StartAssetEditing)
		// =====================================================================

		private class MaskCandidate
		{
			public string  featureName;
			public string  maskChannelProp; // final prefixed NonToon property name
			public bool    isEmission;      // emission = never dropped
			public Texture srcMask;         // source mask texture (may be null → white)
			public string  srcMaskProperty; // lilToon texture property (for UV/ST preservation)
		}

		private class EditableMaskChannel
		{
			public Texture2D texture;
			public int       mode;          // Shader Core ChannelMode enum index
			public Vector4   blend;
			public float     fallbackValue = 1f;
		}

		private const int ShaderCoreMaskModeR         = 0;
		private const int ShaderCoreMaskModeLuminance = 9;
		private const int ShaderCoreMaskModeCustom    = 10;
		// LightBoost is an absolute light-color floor, not an additive/multiplicative amount.
		// A low neutral-light baseline avoids the severe over-brightening caused by adding 1.0.
		private const float NonToonEmissionLightBaseline = 0.125f;

		// =====================================================================
		// Pre-pass: resolve mask overflow (called outside StartAssetEditing)
		// =====================================================================

		private static Dictionary<Material, List<MaskCandidate>> PreResolveMaskOverflow(
			List<ConversionSlot> slots, ConverterOptions opt)
		{
			var result = new Dictionary<Material, List<MaskCandidate>>();

			foreach (var slot in slots)
			{
				var src = slot.material;
				var candidates = CollectMaskCandidates(src, opt);
				if (candidates.Count == 0) { result[src] = candidates; continue; }

				if (candidates.Count > 4)
				{
					// Emission is always first; others sorted after
					var emission = candidates.Where(c => c.isEmission).ToList();
					var others   = candidates.Where(c => !c.isEmission).ToList();

					if (emission.Count + others.Count > 4)
					{
						int maxOthers = 4 - emission.Count;
						var dropped = others.Skip(maxOthers).ToList();

						// Build dialog message
						var msg = new System.Text.StringBuilder();
						msg.Append($"Material '{src.name}' has {candidates.Count} mask candidates but SharedMask only holds 4 channels.\n");
						msg.Append("Emission is kept (priority).\n\nThe following will be dropped:\n");
						foreach (var d in dropped)
							msg.Append($"  - {d.featureName}\n");
						msg.Append("\nClick OK to proceed, or Cancel to skip mask packing for this material.");

						bool ok = EditorUtility.DisplayDialog(
							"Mask Overflow", msg.ToString(), "OK", "Cancel");

						if (ok)
							candidates = emission.Concat(others.Take(maxOthers)).ToList();
						else
							candidates = new List<MaskCandidate>(); // skip packing
					}
				}

				result[src] = candidates;
			}
			return result;
		}

		// Below this alpha, lilToon's emission is effectively invisible — treat it as OFF (B1 fix).
		private const float EmissionAlphaThreshold = 0.01f;

		private static bool HasEmissionAlpha(Material src)
		{
			// lilToon's effective emission strength includes HDR color, color alpha and the
			// separate _EmissionBlend slider. Skip only when that combined energy is negligible.
			if (!src.HasProperty("_EmissionColor")) return true;
			Color color = src.GetColor("_EmissionColor");
			float luminance = 0.2126729f * Mathf.Max(0f, color.r) +
			                  0.7151522f * Mathf.Max(0f, color.g) +
			                  0.0721750f * Mathf.Max(0f, color.b);
			bool standardEmission = src.HasProperty("_EmissionBlend");
			float alpha = standardEmission ? Mathf.Max(0f, color.a) : 1f; // lilToon Lite ignores emission alpha.
			float blend = standardEmission ? Mathf.Clamp01(src.GetFloat("_EmissionBlend")) : 1f;
			return luminance * alpha * blend > EmissionAlphaThreshold;
		}

		private static List<MaskCandidate> CollectMaskCandidates(Material src, ConverterOptions opt)
		{
			var list = new List<MaskCandidate>();

			// 1. Emission mask — PRIORITY (gated on alpha: lilToon _EmissionColor.a is the emission
			//    strength; near-zero alpha means emission is effectively off, so don't port it).
			if (opt.BakeEmission && IsFeatureActive(src, "_UseEmission") && HasEmissionAlpha(src))
			{
				list.Add(new MaskCandidate
				{
					featureName     = "Emission",
					maskChannelProp = P_Lighten + "LightBoostMaskChannel",
					isEmission      = true,
					srcMask         = src.HasProperty("_EmissionBlendMask") ? src.GetTexture("_EmissionBlendMask") : null,
					srcMaskProperty = "_EmissionBlendMask",
				});
			}

			// Emission needs a SharedMask channel too, but it is controlled by its own option.
			// Turning general mask conversion off should not silently disable emission conversion.
			if (!opt.BakeMasks) return list;

			// 2 & 3. MatCap blend masks — routed to whichever NonToon slot (Multiply/Add) the matcap
			// itself was routed to (see DecideMatCapRouting); the 2nd matcap's mask is skipped when
			// the 2nd matcap itself was dropped (same blend mode as the 1st).
			DecideMatCapRouting(src, out bool mcUse1, out bool mcMul1, out bool mcUse2, out bool mcMul2, out _);
			if (mcUse1 && HasNonNullTex(src, "_MatCapBlendMask"))
			{
				list.Add(new MaskCandidate
				{
					featureName     = mcMul1 ? "MatCap Multiply Mask" : "MatCap Add Mask",
					maskChannelProp = P_MatCaps + (mcMul1 ? "MatCapMultiplyMaskChannel" : "MatCapAddMaskChannel"),
					srcMask         = src.GetTexture("_MatCapBlendMask"),
					srcMaskProperty = "_MatCapBlendMask",
				});
			}
			if (mcUse2 && HasNonNullTex(src, "_MatCap2ndBlendMask"))
			{
				list.Add(new MaskCandidate
				{
					featureName     = mcMul2 ? "MatCap Multiply Mask" : "MatCap Add Mask",
					maskChannelProp = P_MatCaps + (mcMul2 ? "MatCapMultiplyMaskChannel" : "MatCapAddMaskChannel"),
					srcMask         = src.GetTexture("_MatCap2ndBlendMask"),
					srcMaskProperty = "_MatCap2ndBlendMask",
				});
			}

			// 4. RimLight mask (via _RimColorTex as grayscale or dedicated mask)
			if (IsFeatureActive(src, "_UseRim") && HasNonNullTex(src, "_RimColorTex"))
			{
				list.Add(new MaskCandidate
				{
					featureName     = "RimLight Mask",
					maskChannelProp = P_RimLight + "RimLightMaskChannel",
					srcMask         = src.GetTexture("_RimColorTex"),
					srcMaskProperty = "_RimColorTex",
				});
			}

			// 5. Outline width mask
			if (HasNonNullTex(src, "_OutlineWidthMask"))
			{
				list.Add(new MaskCandidate
				{
					featureName     = "Outline Width Mask",
					maskChannelProp = null, // NonToon has no per-pixel outline width — stored for info
					srcMask         = src.GetTexture("_OutlineWidthMask"),
					srcMaskProperty = "_OutlineWidthMask",
				});
			}

			// Remove candidates with null maskChannelProp (no NonToon target)
			list = list.Where(c => !string.IsNullOrEmpty(c.maskChannelProp)).ToList();
			return list;
		}

		// =====================================================================
		// Bake orchestrator (runs inside StartAssetEditing)
		// =====================================================================

		private static void RunBakes(Material src, Material dst, ConverterOptions opt,
		                              string folder, string baseName, List<MaskCandidate> resolvedMasks)
		{
			// --- Main texture bake (folds in _Color tint + tiling + 2nd/3rd layers) ---
			if (opt.BakeMainTex || opt.BakeAlphaMask)
			{
				var bakedMain = BakeMainTexture(src, folder, baseName, opt.BakeMainTex, opt.BakeAlphaMask);
				if (bakedMain != null && dst.HasProperty("_BaseTexture"))
					dst.SetTexture("_BaseTexture", bakedMain);
			}

			// --- Solid-color fallback (ALWAYS — NonToon has no color multiply) ---
			// When the source has no real main texture but a non-white _Color, _BaseTexture would
			// render white. Bake a 4x4 solid-color texture so the tint survives.
			{
				var mainTex = src.HasProperty("_MainTex") ? src.GetTexture("_MainTex") : null;
				bool hasColor = src.HasProperty("_Color") && src.GetColor("_Color") != Color.white;
				if (mainTex == null && hasColor && dst.HasProperty("_BaseTexture"))
				{
					var solid = BakeSolidColor(src.GetColor("_Color"), folder, baseName);
					if (solid != null) dst.SetTexture("_BaseTexture", solid);
				}
			}

			// --- Normal map bake (tiling/offset; gated on _UseBumpMap) ---
			if (opt.BakeNormalMap && IsFeatureActive(src, "_UseBumpMap"))
			{
				var bakedNorm = BakeNormalMap(src, folder, baseName);
				if (bakedNorm != null && dst.HasProperty("_NormalMap"))
					dst.SetTexture("_NormalMap", bakedNorm);
			}

			// --- Shadow → SharedGradients ---
			if (opt.BakeShadow)
			{
				var gradArray = BakeShadowGradient(src, folder, baseName);
				if (gradArray != null && dst.HasProperty("_SharedGradients"))
				{
					dst.SetTexture("_SharedGradients", gradArray);
					SetIntIfExists(dst, P_Shade + "ShadeGradientIndex", 0);
					if (dst.HasProperty(P_Shade + "ShadeGradientRange"))
						dst.SetVector(P_Shade + "ShadeGradientRange", new Vector4(0f, 1f, 0f, 0f));
				}
			}

			// --- SharedMask packing (emission + optional masks) ---
			if (resolvedMasks != null && resolvedMasks.Count > 0)
			{
				PackSharedMask(src, dst, resolvedMasks, folder, baseName);
			}
		}

		// =====================================================================
		// Main texture bake (reuses lilToon's Hidden/ltsother_baker)
		// =====================================================================

		private static Texture2D BakeMainTexture(Material src, string folder, string baseName,
		                                                bool bakeMainTexture, bool bakeAlphaMask)
		{
			var srcMain = src.HasProperty("_MainTex") ? src.GetTexture("_MainTex") as Texture2D : null;
			// No base texture → solid-color path handles it.
			if (srcMain == null) return null;

			bool hasColor  = src.HasProperty("_Color")  && src.GetColor("_Color") != Color.white;
			bool hasHSVG   = src.HasProperty("_MainTexHSVG") &&
			                 src.GetVector("_MainTexHSVG") != new Vector4(0f, 1f, 1f, 1f);
			bool has2nd    = IsFeatureActive(src, "_UseMain2ndTex");
			bool has3rd    = IsFeatureActive(src, "_UseMain3rdTex");
			bool hasGrad   = src.HasProperty("_MainGradationStrength") && src.GetFloat("_MainGradationStrength") > 0f;
			var  mainST    = src.HasProperty("_MainTex") ? new Vector4(
			                    src.GetTextureScale("_MainTex").x, src.GetTextureScale("_MainTex").y,
			                    src.GetTextureOffset("_MainTex").x, src.GetTextureOffset("_MainTex").y)
			                 : new Vector4(1, 1, 0, 0);
			bool hasTiling = !(Mathf.Approximately(mainST.x, 1f) && Mathf.Approximately(mainST.y, 1f) &&
			                   Mathf.Approximately(mainST.z, 0f) && Mathf.Approximately(mainST.w, 0f));
			bool shouldBakeMain  = bakeMainTexture && (hasColor || hasHSVG || has2nd || has3rd || hasGrad || hasTiling);
			bool shouldBakeAlpha = bakeAlphaMask && ShouldBakeAlphaMask(src);

			// Nothing to composite → keep the original texture mapping.
			if (!shouldBakeMain && !shouldBakeAlpha)
				return null;

			var bakerShader = Shader.Find("Hidden/ltsother_baker");
			if (bakerShader == null)
			{
				Debug.LogWarning("[YNC] Hidden/ltsother_baker not found — skipping main texture bake");
				return null;
			}

			var bakerMat = new Material(bakerShader);
			var mainFull = LoadFullResReadable(srcMain);
			Texture2D alphaMaskFull = null;
			Texture2D workingTex = mainFull;
			Texture2D generatedTex = null;
			try
			{
				if (shouldBakeMain)
				{
					bakerMat.SetColor("_Color", hasColor ? src.GetColor("_Color") : Color.white);
					if (src.HasProperty("_MainTexHSVG"))           bakerMat.SetVector("_MainTexHSVG",           src.GetVector("_MainTexHSVG"));
					if (src.HasProperty("_MainGradationStrength")) bakerMat.SetFloat("_MainGradationStrength",  src.GetFloat("_MainGradationStrength"));
					if (src.HasProperty("_MainGradationTex"))      bakerMat.SetTexture("_MainGradationTex",     src.GetTexture("_MainGradationTex"));
					if (src.HasProperty("_MainColorAdjustMask"))   bakerMat.SetTexture("_MainColorAdjustMask",  src.GetTexture("_MainColorAdjustMask"));

					bakerMat.SetTexture("_MainTex", mainFull);
					// Base tiling/offset — baker honors _MainTex_ST via LIL_GET_SUBTEX.
					bakerMat.SetTextureScale("_MainTex",  new Vector2(mainST.x, mainST.y));
					bakerMat.SetTextureOffset("_MainTex", new Vector2(mainST.z, mainST.w));

					if (has2nd) CopyMain2ndToBaker(src, bakerMat);
					if (has3rd) CopyMain3rdToBaker(src, bakerMat);

					generatedTex = RunBlit(bakerMat, mainFull, mainFull.width, mainFull.height, false);
					workingTex = generatedTex;
				}

				if (shouldBakeAlpha)
				{
					// lilToon's alpha-mask baker is a separate keyword path. Run it after the
					// main pass so Color/HSVG/2nd/3rd compositing is retained.
					bakerMat.EnableKeyword("_ALPHAMASK");
					bakerMat.SetTexture("_MainTex", workingTex);
					bakerMat.SetTextureScale("_MainTex", Vector2.one);
					bakerMat.SetTextureOffset("_MainTex", Vector2.zero);
					// Match lilToon's AutoBakeAlphaMask: the hidden baker keeps this Int-declared
					// property in its float property sheet, so SetInteger logs a type collision.
					bakerMat.SetFloat("_AlphaMaskMode", src.GetFloat("_AlphaMaskMode"));
					bakerMat.SetFloat("_AlphaMaskScale", src.HasProperty("_AlphaMaskScale") ? src.GetFloat("_AlphaMaskScale") : 1f);
					bakerMat.SetFloat("_AlphaMaskValue", src.HasProperty("_AlphaMaskValue") ? src.GetFloat("_AlphaMaskValue") : 0f);

					var alphaMask = src.GetTexture("_AlphaMask");
					if (alphaMask is Texture2D alphaMask2D)
					{
						alphaMaskFull = LoadFullResReadable(alphaMask2D);
						alphaMask = alphaMaskFull;
					}
					bakerMat.SetTexture("_AlphaMask", alphaMask);

					var alphaBaked = RunBlit(bakerMat, workingTex, mainFull.width, mainFull.height, false);
					if (generatedTex != null) Object.DestroyImmediate(generatedTex);
					generatedTex = alphaBaked;
					workingTex = generatedTex;
				}

				var saved = SaveTexturePng(workingTex, folder, baseName + "_NTBake");
				if (saved != null)
					CopyImportSettings(srcMain, saved, asNormal: false, alphaIsTransparency: shouldBakeAlpha);
				return saved;
			}
			finally
			{
				Object.DestroyImmediate(bakerMat);
				if (generatedTex != null) Object.DestroyImmediate(generatedTex);
				if (alphaMaskFull != null && alphaMaskFull != src.GetTexture("_AlphaMask")) Object.DestroyImmediate(alphaMaskFull);
				if (mainFull != null && mainFull != srcMain) Object.DestroyImmediate(mainFull);
			}
		}

		/// <summary>
		/// Mirrors lilToon's alpha-mask controls. The editor exposes an effective Transparency
		/// value in [-1, 1]; either endpoint (and an extreme cutoff) produces a uniform result,
		/// so there is no useful mask detail to fold into the texture.
		/// </summary>
		private static bool ShouldBakeAlphaMask(Material src)
		{
			if (src == null || !src.HasProperty("_AlphaMaskMode") ||
			    Mathf.RoundToInt(src.GetFloat("_AlphaMaskMode")) == 0 ||
			    !src.HasProperty("_AlphaMask") || src.GetTexture("_AlphaMask") == null)
				return false;

			float scale = src.HasProperty("_AlphaMaskScale") ? src.GetFloat("_AlphaMaskScale") : 1f;
			float value = src.HasProperty("_AlphaMaskValue") ? src.GetFloat("_AlphaMaskValue") : 0f;
			float transparency = value - (scale < 0f ? 1f : 0f);
			if (transparency <= -1f || transparency >= 1f) return false;

			if (src.HasProperty("_Cutoff"))
			{
				float cutoff = src.GetFloat("_Cutoff");
				if (cutoff <= -1f || cutoff >= 1f) return false;
			}

			return true;
		}

		private static void CopyMain2ndToBaker(Material src, Material dst)
		{
			// NOTE: _Color2nd is a COLOR (its alpha = blend strength) — must use SetColor, not SetFloat.
			if (src.HasProperty("_Color2nd")) dst.SetColor("_Color2nd", src.GetColor("_Color2nd"));
			var props2nd = new[]
			{
				"_UseMain2ndTex", "_Main2ndTexAngle", "_Main2ndTexIsDecal",
				"_Main2ndTexIsLeftOnly", "_Main2ndTexIsRightOnly", "_Main2ndTexShouldCopy",
				"_Main2ndTexShouldFlipMirror", "_Main2ndTexShouldFlipCopy", "_Main2ndTexIsMSDF",
				"_Main2ndTexBlendMode", "_Main2ndTexAlphaMode",
			};
			foreach (var p in props2nd)
				if (src.HasProperty(p)) dst.SetFloat(p, src.GetFloat(p));
			if (src.HasProperty("_Main2ndTexDecalAnimation")) dst.SetVector("_Main2ndTexDecalAnimation", src.GetVector("_Main2ndTexDecalAnimation"));
			if (src.HasProperty("_Main2ndTexDecalSubParam"))  dst.SetVector("_Main2ndTexDecalSubParam",  src.GetVector("_Main2ndTexDecalSubParam"));
			if (src.HasProperty("_Main2ndTex"))
			{
				var t = src.GetTexture("_Main2ndTex") as Texture2D;
				if (t != null) { dst.SetTexture("_Main2ndTex", LoadFullResReadable(t)); dst.SetTextureScale("_Main2ndTex", src.GetTextureScale("_Main2ndTex")); dst.SetTextureOffset("_Main2ndTex", src.GetTextureOffset("_Main2ndTex")); }
			}
			if (src.HasProperty("_Main2ndBlendMask"))
			{
				var t = src.GetTexture("_Main2ndBlendMask") as Texture2D;
				if (t != null) { dst.SetTexture("_Main2ndBlendMask", LoadFullResReadable(t)); dst.SetTextureScale("_Main2ndBlendMask", src.GetTextureScale("_Main2ndBlendMask")); dst.SetTextureOffset("_Main2ndBlendMask", src.GetTextureOffset("_Main2ndBlendMask")); }
			}
		}

		private static void CopyMain3rdToBaker(Material src, Material dst)
		{
			// _Color3rd is a COLOR (alpha = blend strength) — must use SetColor.
			if (src.HasProperty("_Color3rd")) dst.SetColor("_Color3rd", src.GetColor("_Color3rd"));
			var props3rd = new[]
			{
				"_UseMain3rdTex", "_Main3rdTexAngle", "_Main3rdTexIsDecal",
				"_Main3rdTexIsLeftOnly", "_Main3rdTexIsRightOnly", "_Main3rdTexShouldCopy",
				"_Main3rdTexShouldFlipMirror", "_Main3rdTexShouldFlipCopy", "_Main3rdTexIsMSDF",
				"_Main3rdTexBlendMode", "_Main3rdTexAlphaMode",
			};
			foreach (var p in props3rd)
				if (src.HasProperty(p)) dst.SetFloat(p, src.GetFloat(p));
			if (src.HasProperty("_Main3rdTexDecalAnimation")) dst.SetVector("_Main3rdTexDecalAnimation", src.GetVector("_Main3rdTexDecalAnimation"));
			if (src.HasProperty("_Main3rdTexDecalSubParam"))  dst.SetVector("_Main3rdTexDecalSubParam",  src.GetVector("_Main3rdTexDecalSubParam"));
			if (src.HasProperty("_Main3rdTex"))
			{
				var t = src.GetTexture("_Main3rdTex") as Texture2D;
				if (t != null) { dst.SetTexture("_Main3rdTex", LoadFullResReadable(t)); dst.SetTextureScale("_Main3rdTex", src.GetTextureScale("_Main3rdTex")); dst.SetTextureOffset("_Main3rdTex", src.GetTextureOffset("_Main3rdTex")); }
			}
			if (src.HasProperty("_Main3rdBlendMask"))
			{
				var t = src.GetTexture("_Main3rdBlendMask") as Texture2D;
				if (t != null) { dst.SetTexture("_Main3rdBlendMask", LoadFullResReadable(t)); dst.SetTextureScale("_Main3rdBlendMask", src.GetTextureScale("_Main3rdBlendMask")); dst.SetTextureOffset("_Main3rdBlendMask", src.GetTextureOffset("_Main3rdBlendMask")); }
			}
		}

		// =====================================================================
		// Normal map bake (applies tiling/offset)
		// =====================================================================

		private static Texture2D BakeNormalMap(Material src, string folder, string baseName)
		{
			if (!src.HasProperty("_BumpMap")) return null;
			var normTex = src.GetTexture("_BumpMap") as Texture2D;
			if (normTex == null) return null;

			// Tiling/offset — read from the material's texture scale/offset (== _BumpMap_ST).
			var scale  = src.GetTextureScale("_BumpMap");
			var offset = src.GetTextureOffset("_BumpMap");
			bool stDefault = Mathf.Approximately(scale.x, 1f) && Mathf.Approximately(scale.y, 1f) &&
			                 Mathf.Approximately(offset.x, 0f) && Mathf.Approximately(offset.y, 0f);
			if (stDefault) return null; // no bake needed; mapping assigns the original

			// Bake at full source resolution, tiling applied via the scale/offset Blit overload,
			// into a LINEAR RT so normal-map values are preserved.
			var full = LoadFullResReadable(normTex);
			try
			{
				var rt   = RenderTexture.GetTemporary(full.width, full.height, 0,
				           RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
				rt.wrapMode = TextureWrapMode.Repeat;
				var prev = RenderTexture.active;
				Graphics.Blit(full, rt, new Vector2(scale.x, scale.y), new Vector2(offset.x, offset.y));
				RenderTexture.active = rt;
				var outTex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
				outTex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
				outTex.Apply();
				RenderTexture.active = prev;
				RenderTexture.ReleaseTemporary(rt);

				var saved = SaveTexturePng(outTex, folder, baseName + "_Normal_NTBake", isNormalMap: false);
				if (saved != null) CopyImportSettings(normTex, saved, asNormal: true);
				return saved;
			}
			finally { if (full != null && full != normTex) Object.DestroyImmediate(full); }
		}

		// =====================================================================
		// Shadow → SharedGradients (Texture2DArray ramp)
		// =====================================================================

		private static Texture2DArray BakeShadowGradient(Material src, string folder, string baseName)
		{
			if (!IsFeatureActive(src, "_UseShadow")) return null;

			Color shadowColor = src.HasProperty("_ShadowColor") ? src.GetColor("_ShadowColor") : new Color(0.5f, 0.5f, 0.5f, 1f);

			var gradient = new Gradient
			{
				mode = GradientMode.Blend
			};
			gradient.SetKeys(
				new[]
				{
					new GradientColorKey(shadowColor, 0f),
					new GradientColorKey(Color.white, 1f),
				},
				new[]
				{
					new GradientAlphaKey(shadowColor.a, 0f),
					new GradientAlphaKey(1f, 1f),
				});

			string path = AssetDatabase.GenerateUniqueAssetPath(
				Path.Combine(folder, baseName + "_Shade_NTBake.scgradients").Replace('\\', '/'));
			try
			{
				File.WriteAllBytes(path, System.Array.Empty<byte>());
				AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

				var importer = AssetImporter.GetAtPath(path);
				if (importer == null)
					throw new System.InvalidOperationException("Shader Core's .scgradients importer was not found.");

				var serializedImporter = new SerializedObject(importer);
				serializedImporter.Update();
				var sizeProp = serializedImporter.FindProperty("size");
				var gradientsProp = serializedImporter.FindProperty("gradients");
				if (sizeProp == null || gradientsProp == null || !gradientsProp.isArray)
					throw new System.InvalidOperationException("The imported asset is not using Shader Core's GradientsImporter schema.");

				sizeProp.intValue = 128;
				gradientsProp.arraySize = 1;
				var gradientProp = gradientsProp.GetArrayElementAtIndex(0);
				if (gradientProp == null)
					throw new System.InvalidOperationException("Shader Core did not expose a gradient element.");
				gradientProp.gradientValue = gradient;
				serializedImporter.ApplyModifiedPropertiesWithoutUndo();
				importer.SaveAndReimport();

				var result = AssetDatabase.LoadAssetAtPath<Texture2DArray>(path);
				if (result == null)
					throw new System.InvalidOperationException("Shader Core did not generate a Texture2DArray.");
				return result;
			}
			catch (System.Exception ex)
			{
				Debug.LogError($"[YNC] Could not create editable shadow gradients at {path}: {ex.Message}");
				if (!AssetDatabase.DeleteAsset(path) && File.Exists(path)) File.Delete(path);
				return null;
			}
		}

		// =====================================================================
		// Editable SharedMask asset
		// =====================================================================

		private static void PackSharedMask(Material src, Material dst, List<MaskCandidate> candidates,
		                                    string folder, string baseName)
		{
			if (dst == null || !dst.HasProperty("_SharedMask")) return;

			var channels = new List<EditableMaskChannel>();
			int channelCount = Mathf.Min(candidates.Count, 4);
			for (int ch = 0; ch < channelCount; ch++)
			{
				channels.Add(PrepareEditableMaskChannel(src, candidates[ch], folder, baseName, ch));
			}

			var sharedMask = CreateEditableSharedMask(channels, folder, baseName + "_Mask_NTBake");
			if (sharedMask == null) return;

			// Commit feature channel indices only after the editable asset imported successfully.
			// Otherwise NonToon's default white mask could turn a failed emission conversion global.
			dst.SetTexture("_SharedMask", sharedMask);
			for (int ch = 0; ch < channelCount; ch++)
			{
				var cand = candidates[ch];
				SetIntIfExists(dst, cand.maskChannelProp, ch);
				if (!cand.isEmission) continue;

				SetIntIfExists(dst, P_Lighten + "LightBoostAsEmission", 1);
				float lightBoost = CalculateEmissionLightBoost(src);
				SetFloatIfExists(dst, P_Lighten + "LightBoost", lightBoost);
				Debug.Log($"[YNC] Emission ported to Lighten with Light Boost {lightBoost:0.###} " +
				          "(grayscale approximation — color information is lost).");
			}
		}

		private static EditableMaskChannel PrepareEditableMaskChannel(Material src, MaskCandidate candidate,
		                                                              string folder, string baseName, int channelIndex)
		{
			if (!candidate.isEmission)
			{
				if (candidate.srcMask is Texture2D direct &&
				    CanLinkUvMainTextureDirectly(src, candidate.srcMaskProperty, direct))
					return new EditableMaskChannel { texture = direct, mode = ShaderCoreMaskModeR };

				if (candidate.srcMask == null)
					return new EditableMaskChannel { mode = ShaderCoreMaskModeR, fallbackValue = 1f };

				GetUvMainTextureTransform(src, candidate.srcMaskProperty, out Vector2 scale, out Vector2 offset);
				var baked = BakeSingleMaskTexture(candidate.srcMask, folder,
					baseName + "_Channel" + channelIndex + "_NTBake", useLuminance: false, scale, offset);
				return new EditableMaskChannel { texture = baked, mode = ShaderCoreMaskModeR };
			}

			Texture emissionMap = GetMeaningfulMaterialTexture(src, "_EmissionMap");
			Texture blendMask = GetMeaningfulMaterialTexture(src, "_EmissionBlendMask");
			var emissionMap2D = emissionMap as Texture2D;
			var blendMask2D = blendMask as Texture2D;

			// Shader Core can perform luminance or R-channel extraction itself. Keep the original
			// source editable whenever only one persistent texture participates in the result.
			if (emissionMap != null && blendMask == null &&
			    CanLinkEmissionTextureDirectly(src, "_EmissionMap", emissionMap2D))
				return CreateDirectEmissionMapChannel(src, emissionMap2D);
			if (emissionMap == null && blendMask != null &&
			    CanLinkEmissionTextureDirectly(src, "_EmissionBlendMask", blendMask2D))
				return CreateDirectEmissionMapChannel(src, blendMask2D);
			if (emissionMap == null && blendMask == null)
				return new EditableMaskChannel { mode = ShaderCoreMaskModeR, fallbackValue = 1f };

			// Map × blend mask (or a non-persistent source) cannot be represented by one .scmask
			// channel, so bake only that spatial product and keep HDR/color strength in LightBoost.
			var composite = BakeEmissionSpatialMask(src, emissionMap, blendMask, folder,
				baseName + "_EmissionComposite_NTBake");
			return new EditableMaskChannel { texture = composite, mode = ShaderCoreMaskModeR };
		}

		private static EditableMaskChannel CreateDirectEmissionMapChannel(Material material, Texture2D texture)
		{
			Color color = material.HasProperty("_EmissionColor") ? material.GetColor("_EmissionColor") : Color.white;
			float r = Mathf.Max(0f, color.r), g = Mathf.Max(0f, color.g), b = Mathf.Max(0f, color.b);
			float luminance = 0.2126729f * r + 0.7151522f * g + 0.0721750f * b;
			if (luminance <= 0.000001f)
				return new EditableMaskChannel { texture = texture, mode = ShaderCoreMaskModeLuminance };

			// Y(C*T)/Y(C) is a linear dot product, so Shader Core's Custom channel mode can
			// preserve colored emission-map energy while still linking the original texture.
			return new EditableMaskChannel
			{
				texture = texture,
				mode = ShaderCoreMaskModeCustom,
				blend = new Vector4(0.2126729f * r / luminance,
				                    0.7151522f * g / luminance,
				                    0.0721750f * b / luminance, 0f),
			};
		}

		private static Texture GetMeaningfulMaterialTexture(Material material, string propertyName)
		{
			if (material == null || !material.HasProperty(propertyName)) return null;
			var texture = material.GetTexture(propertyName);
			if (texture == null || texture == Texture2D.whiteTexture) return null;
			return texture;
		}

		private static bool CanLinkEmissionTextureDirectly(Material material, string propertyName, Texture2D texture)
		{
			bool standardEmission = material.HasProperty("_EmissionBlend");
			if (texture == null || !AssetDatabase.Contains(texture) ||
			    (standardEmission && TextureAlphaRequiresBake(texture)))
				return false;

			if (propertyName == "_EmissionBlendMask")
			{
				// Installed full lilToon variants define ANIMATE_EMISSION_MASK_UV, whose path
				// samples this mask from raw UV0 with its own ST (not fd.uvMain).
				Vector2 maskScale = material.GetTextureScale(propertyName);
				Vector2 maskOffset = material.GetTextureOffset(propertyName);
				return Approximately(maskScale, Vector2.one) && Approximately(maskOffset, Vector2.zero) &&
				       !HasUvAnimation(material, propertyName);
			}

			Vector2 scale = material.GetTextureScale(propertyName);
			Vector2 offset = material.GetTextureOffset(propertyName);
			if (!Approximately(scale, Vector2.one) || !Approximately(offset, Vector2.zero)) return false;
			if (material.HasProperty("_EmissionMap_UVMode") &&
			    Mathf.RoundToInt(material.GetFloat("_EmissionMap_UVMode")) != 0) return false;
			if (material.HasProperty("_EmissionParallaxDepth") &&
			    !Mathf.Approximately(material.GetFloat("_EmissionParallaxDepth"), 0f)) return false;

			return !HasUvAnimation(material, propertyName);
		}

		private static bool CanLinkUvMainTextureDirectly(Material material, string propertyName, Texture2D texture)
		{
			if (material == null || texture == null || !AssetDatabase.Contains(texture) ||
			    string.IsNullOrEmpty(propertyName)) return false;

			GetUvMainTextureTransform(material, propertyName, out Vector2 scale, out Vector2 offset);
			return Approximately(scale, Vector2.one) && Approximately(offset, Vector2.zero) &&
			       !HasUvAnimation(material, "_MainTex") && !HasUvAnimation(material, propertyName);
		}

		private static void GetUvMainTextureTransform(Material material, string propertyName,
		                                              out Vector2 scale, out Vector2 offset)
		{
			Vector2 mainScale = material != null && material.HasProperty("_MainTex")
				? material.GetTextureScale("_MainTex") : Vector2.one;
			Vector2 mainOffset = material != null && material.HasProperty("_MainTex")
				? material.GetTextureOffset("_MainTex") : Vector2.zero;
			Vector2 textureScale = material != null && !string.IsNullOrEmpty(propertyName) && material.HasProperty(propertyName)
				? material.GetTextureScale(propertyName) : Vector2.one;
			Vector2 textureOffset = material != null && !string.IsNullOrEmpty(propertyName) && material.HasProperty(propertyName)
				? material.GetTextureOffset(propertyName) : Vector2.zero;

			scale = Vector2.Scale(mainScale, textureScale);
			offset = Vector2.Scale(mainOffset, textureScale) + textureOffset;
		}

		private static bool HasUvAnimation(Material material, string propertyName)
		{
			string animationProperty = propertyName + "_ScrollRotate";
			return material != null && material.HasProperty(animationProperty) &&
			       material.GetVector(animationProperty) != Vector4.zero;
		}

		private static bool Approximately(Vector2 a, Vector2 b)
		{
			return Mathf.Approximately(a.x, b.x) && Mathf.Approximately(a.y, b.y);
		}

		private static bool TextureAlphaRequiresBake(Texture2D texture)
		{
			var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) as TextureImporter;
			if (importer == null) return true; // Unknown generated texture: preserve correctness conservatively.
			if (importer.alphaSource == TextureImporterAlphaSource.None) return false;
			if (importer.alphaSource == TextureImporterAlphaSource.FromGrayScale) return true;
			return importer.DoesSourceTextureHaveAlpha();
		}

		private static Texture2D BakeSingleMaskTexture(Texture source, string folder, string name,
		                                               bool useLuminance, Vector2? scale = null,
		                                               Vector2? offset = null)
		{
			if (source == null) return null;
			int width = Mathf.Max(4, source.width);
			int height = Mathf.Max(4, source.height);
			var pixels = SampleMaskGrayscale(source, width, height, useLuminance, scale, offset);
			if (pixels == null) return null;

			var output = new Texture2D(width, height, TextureFormat.RGBA32, false);
			for (int i = 0; i < pixels.Length; i++)
			{
				float value = pixels[i].r;
				pixels[i] = new Color(value, value, value, 1f);
			}
			output.SetPixels(pixels);
			output.Apply();
			return SaveTexturePng(output, folder, name);
		}

		private static Texture2D BakeEmissionSpatialMask(Material material, Texture emissionMap, Texture blendMask,
		                                                        string folder, string name)
		{
			int width = 4, height = 4;
			if (emissionMap != null) { width = Mathf.Max(width, emissionMap.width); height = Mathf.Max(height, emissionMap.height); }
			if (blendMask != null) { width = Mathf.Max(width, blendMask.width); height = Mathf.Max(height, blendMask.height); }

			var emissionPixels = emissionMap != null
				? SampleMaskGrayscale(emissionMap, width, height, useLuma: false,
					material.GetTextureScale("_EmissionMap"), material.GetTextureOffset("_EmissionMap"))
				: null;
			var blendPixels = blendMask != null
				? SampleMaskGrayscale(blendMask, width, height, useLuma: false,
					material.GetTextureScale("_EmissionBlendMask"), material.GetTextureOffset("_EmissionBlendMask"))
				: null;
			Color emissionColor = material.HasProperty("_EmissionColor") ? material.GetColor("_EmissionColor") : Color.white;
			float colorR = Mathf.Max(0f, emissionColor.r), colorG = Mathf.Max(0f, emissionColor.g), colorB = Mathf.Max(0f, emissionColor.b);
			float colorLuminance = 0.2126729f * colorR + 0.7151522f * colorG + 0.0721750f * colorB;
			Vector3 weights = colorLuminance > 0.000001f
				? new Vector3(0.2126729f * colorR / colorLuminance,
				              0.7151522f * colorG / colorLuminance,
				              0.0721750f * colorB / colorLuminance)
				: new Vector3(0.2126729f, 0.7151522f, 0.0721750f);

			var outputPixels = new Color[width * height];
			for (int i = 0; i < outputPixels.Length; i++)
			{
				Color map = emissionPixels == null ? Color.white : emissionPixels[i];
				Color mask = blendPixels == null ? Color.white : blendPixels[i];
				float alphaFactor = material.HasProperty("_EmissionBlend") ? map.a * mask.a : 1f;
				float value = Mathf.Clamp01(
					(weights.x * map.r * mask.r + weights.y * map.g * mask.g + weights.z * map.b * mask.b) *
					alphaFactor);
				outputPixels[i] = new Color(value, value, value, 1f);
			}

			var output = new Texture2D(width, height, TextureFormat.RGBA32, false);
			output.SetPixels(outputPixels);
			output.Apply();
			return SaveTexturePng(output, folder, name);
		}

		private static float CalculateEmissionLightBoost(Material src)
		{
			Color color = src.HasProperty("_EmissionColor") ? src.GetColor("_EmissionColor") : Color.white;
			float luminance = 0.2126729f * Mathf.Max(0f, color.r) +
			                  0.7151522f * Mathf.Max(0f, color.g) +
			                  0.0721750f * Mathf.Max(0f, color.b);
			bool standardEmission = src.HasProperty("_EmissionBlend");
			float alpha = standardEmission ? Mathf.Max(0f, color.a) : 1f;
			float blend = standardEmission ? Mathf.Clamp01(src.GetFloat("_EmissionBlend")) : 1f;

			// NonToon applies LightBoost as an absolute lighting floor. Using 1.0 as the
			// baseline over-brightens typical avatar/world lighting; 1/8 plus the source
			// emission energy better matches lilToon's default Add emission in practice.
			// SetFloat may retain HDR values above the inspector's nominal 0..10 slider range.
			return Mathf.Min(65500f, NonToonEmissionLightBaseline + luminance * alpha * blend);
		}

		private static Texture2D CreateEditableSharedMask(IList<EditableMaskChannel> channels,
		                                                        string folder, string name)
		{
			string path = AssetDatabase.GenerateUniqueAssetPath(
				Path.Combine(folder, name + ".scmask").Replace('\\', '/'));
			try
			{
				File.WriteAllBytes(path, System.Array.Empty<byte>());
				AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

				var importer = AssetImporter.GetAtPath(path);
				if (importer == null)
					throw new System.InvalidOperationException("Shader Core's .scmask importer was not found.");

				var serializedImporter = new SerializedObject(importer);
				serializedImporter.Update();
				var widthProp = serializedImporter.FindProperty("width");
				var heightProp = serializedImporter.FindProperty("height");
				if (widthProp == null || heightProp == null)
					throw new System.InvalidOperationException("The imported asset is not using Shader Core's MaskImporter schema.");

				int width = 32, height = 32;
				foreach (var channel in channels)
				{
					if (channel?.texture == null) continue;
					width = Mathf.Max(width, channel.texture.width);
					height = Mathf.Max(height, channel.texture.height);
				}
				widthProp.intValue = Mathf.Clamp(Mathf.NextPowerOfTwo(width), 32, 8192);
				heightProp.intValue = Mathf.Clamp(Mathf.NextPowerOfTwo(height), 32, 8192);

				string[] channelNames = { "R", "G", "B", "A" };
				var textureProps = new SerializedProperty[channelNames.Length];
				var modeProps = new SerializedProperty[channelNames.Length];
				var blendProps = new SerializedProperty[channelNames.Length];
				var fallbackProps = new SerializedProperty[channelNames.Length];
				for (int i = 0; i < channelNames.Length; i++)
				{
					var channelProp = serializedImporter.FindProperty(channelNames[i]);
					textureProps[i] = channelProp?.FindPropertyRelative("tex");
					modeProps[i] = channelProp?.FindPropertyRelative("mode");
					blendProps[i] = channelProp?.FindPropertyRelative("blend");
					fallbackProps[i] = channelProp?.FindPropertyRelative("fallbackValue");
					if (textureProps[i] == null || modeProps[i] == null || blendProps[i] == null || fallbackProps[i] == null)
						throw new System.InvalidOperationException($"Shader Core's {channelNames[i]} mask channel schema is unavailable.");
				}

				for (int i = 0; i < channelNames.Length; i++)
				{
					var data = i < channels.Count && channels[i] != null
						? channels[i]
						: new EditableMaskChannel { mode = ShaderCoreMaskModeR, fallbackValue = 1f };
					textureProps[i].objectReferenceValue = data.texture;
					modeProps[i].enumValueIndex = data.mode;
					blendProps[i].vector4Value = data.blend;
					fallbackProps[i].floatValue = data.fallbackValue;
				}

				serializedImporter.ApplyModifiedPropertiesWithoutUndo();
				importer.SaveAndReimport();
				var result = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
				if (result == null)
					throw new System.InvalidOperationException("Shader Core did not generate a Texture2D.");
				return result;
			}
			catch (System.Exception ex)
			{
				Debug.LogError($"[YNC] Could not create editable SharedMask at {path}: {ex.Message}");
				if (!AssetDatabase.DeleteAsset(path) && File.Exists(path)) File.Delete(path);
				return null;
			}
		}

		private static Color[] SampleMaskGrayscale(Texture src, int targetW, int targetH, bool useLuma = false,
		                                           Vector2? scale = null, Vector2? offset = null)
		{
			if (src == null) return null;
			var prev = RenderTexture.active;
			RenderTexture rt = null;
			Texture2D tmp = null;
			Color[] px;
			try
			{
				rt = RenderTexture.GetTemporary(targetW, targetH, 0, RenderTextureFormat.ARGB32);
				Graphics.Blit(src, rt, scale ?? Vector2.one, offset ?? Vector2.zero);
				RenderTexture.active = rt;
				tmp = new Texture2D(targetW, targetH, TextureFormat.RGBA32, false);
				tmp.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
				tmp.Apply();
				px = tmp.GetPixels();
			}
			finally
			{
				RenderTexture.active = prev;
				if (rt != null) RenderTexture.ReleaseTemporary(rt);
				if (tmp != null) Object.DestroyImmediate(tmp);
			}

			if (!useLuma) return px; // caller reads .r channel
			// Convert to grayscale (luma)
			for (int i = 0; i < px.Length; i++)
			{
				float v = 0.2126729f * px[i].r + 0.7151522f * px[i].g + 0.0721750f * px[i].b;
				px[i] = new Color(v, v, v, px[i].a);
			}
			return px;
		}

		/// <summary>
		/// Bake a matcap texture: optional multi-pass blur (cheap Gaussian matching lilToon's Lod/Blur,
		/// <paramref name="lod"/> = lilToon's _MatCapLod range 0–10) followed by an optional fade toward
		/// white by <paramref name="multiplyStrength"/> (0–1). The fade recovers lilToon's Multiply
		/// blend strength (_MatCapBlend × color.a): NonToon multiplies the base by lerp(1, tex·color,
		/// mask), so pre-fading the texture toward white makes a reduced-alpha lilToon matcap stop
		/// looking glossy. Blur and fade are baked into a SINGLE output asset (no orphan intermediate).
		/// </summary>
		private static Texture2D BakeMatCap(Texture2D src, float lod, float multiplyStrength,
		                                     string folder, string nameNoExt)
		{
			if (src == null) return null;

			var full = LoadFullResReadable(src);
			int w = full.width, h = full.height;

			Texture2D outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);

			if (lod > 0.05f)
			{
				// Map LOD 0-10 to blur passes 1-5 (each pass halves then doubles = 1 blur level).
				int passes  = Mathf.Clamp(Mathf.RoundToInt(lod * 0.5f), 1, 5);
				var current = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
				Graphics.Blit(full, current);
				for (int i = 0; i < passes; i++)
				{
					int sw = Mathf.Max(1, w >> (i + 1)), sh = Mathf.Max(1, h >> (i + 1));
					var smaller = RenderTexture.GetTemporary(sw, sh, 0, RenderTextureFormat.ARGB32);
					Graphics.Blit(current, smaller);          // downsample → blurs via bilinear
					RenderTexture.ReleaseTemporary(current);
					current = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
					Graphics.Blit(smaller, current);          // upsample → soft blur
					RenderTexture.ReleaseTemporary(smaller);
				}
				var prev = RenderTexture.active;
				RenderTexture.active = current;
				outTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
				outTex.Apply();
				RenderTexture.active = prev;
				RenderTexture.ReleaseTemporary(current);
			}
			else
			{
				outTex.SetPixels(full.GetPixels());
				outTex.Apply();
			}

			// Fade toward white by strength (Multiply matcaps only — caller passes 1 otherwise).
			if (multiplyStrength < 0.996f)
			{
				float s   = Mathf.Clamp01(multiplyStrength);
				var   px  = outTex.GetPixels();
				for (int i = 0; i < px.Length; i++)
					px[i] = new Color(Mathf.Lerp(1f, px[i].r, s),
					                  Mathf.Lerp(1f, px[i].g, s),
					                  Mathf.Lerp(1f, px[i].b, s), 1f);
				outTex.SetPixels(px);
				outTex.Apply();
			}

			if (full != null && full != src) Object.DestroyImmediate(full);

			var saved = SaveTexturePng(outTex, folder, nameNoExt + "_MatCap_NTBake");
			if (saved != null) CopyImportSettings(src, saved, asNormal: false);
			return saved;
		}

		// =====================================================================
		// Utilities
		// =====================================================================

		/// <summary>
		/// Load a source texture at FULL FILE RESOLUTION and CPU-readable. For png/jpg/tga we read the
		/// raw asset bytes (bypasses the maxSize import clamp that would make a 2048 source read as 512).
		/// Falls back to a readable RenderTexture copy at the imported size.
		/// </summary>
		private static Texture2D LoadFullResReadable(Texture2D src)
		{
			if (src == null) return Texture2D.whiteTexture;
			string path = AssetDatabase.GetAssetPath(src);
			string ext  = string.IsNullOrEmpty(path) ? "" : Path.GetExtension(path).ToLowerInvariant();
			if (!string.IsNullOrEmpty(path) && (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".tga"))
			{
				try
				{
					var bytes = File.ReadAllBytes(path);
					var tex   = new Texture2D(2, 2, TextureFormat.RGBA32, false);
					if (tex.LoadImage(bytes)) { tex.filterMode = FilterMode.Bilinear; return tex; }
					Object.DestroyImmediate(tex);
				}
				catch (System.Exception e) { Debug.LogWarning($"[YNC] LoadFullResReadable fell back for {path}: {e.Message}"); }
			}
			return RunBlit(null, src, Mathf.Max(2, src.width), Mathf.Max(2, src.height), false);
		}

		/// <summary>
		/// Copy import settings (maxSize / compression / sRGB / format / platform overrides) from a
		/// source texture onto a freshly-baked asset, so the baked texture keeps the source's size and
		/// compression instead of Unity's defaults. Mirrors lilToon's CopyTextureSetting.
		/// </summary>
		private static void CopyImportSettings(Texture2D from, Texture2D toAsset, bool asNormal,
		                                       bool alphaIsTransparency = false)
		{
			string toPath = AssetDatabase.GetAssetPath(toAsset);
			if (!(AssetImporter.GetAtPath(toPath) is TextureImporter toImp)) return;

			string fromPath = from != null ? AssetDatabase.GetAssetPath(from) : null;
			if (!string.IsNullOrEmpty(fromPath) && AssetImporter.GetAtPath(fromPath) is TextureImporter fromImp)
			{
				var settings = new TextureImporterSettings();
				fromImp.ReadTextureSettings(settings);
				toImp.SetTextureSettings(settings);
				toImp.SetPlatformTextureSettings(fromImp.GetDefaultPlatformTextureSettings());
			}
			if (asNormal) toImp.textureType = TextureImporterType.NormalMap;
			if (alphaIsTransparency)
			{
				toImp.alphaSource = TextureImporterAlphaSource.FromInput;
				toImp.alphaIsTransparency = true;
			}
			toImp.SaveAndReimport();
		}

		/// <summary>Create + save a 4x4 solid-color texture (clamped) as `_Color_NTBake.png`.</summary>
		private static Texture2D BakeSolidColor(Color c, string folder, string baseName)
		{
			var col = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), Mathf.Clamp01(c.a));
			var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
			var px  = new Color[16];
			for (int i = 0; i < px.Length; i++) px[i] = col;
			tex.SetPixels(px);
			tex.Apply();
			return SaveTexturePng(tex, folder, baseName + "_Color_NTBake");
		}

		private static Texture2D RunBlit(Material mat, Texture src, int w, int h, bool linear)
		{
			var fmt  = linear ? RenderTextureFormat.ARGB32 : RenderTextureFormat.ARGB32;
			var rw   = linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB;
			var rt   = RenderTexture.GetTemporary(w, h, 0, fmt, rw);
			var prev = RenderTexture.active;
			if (mat != null)
				Graphics.Blit(src, rt, mat);
			else
				Graphics.Blit(src, rt);
			RenderTexture.active = rt;
			var texFmt  = linear ? TextureFormat.RGBA32 : TextureFormat.RGBA32;
			var outTex  = new Texture2D(w, h, texFmt, false, linear);
			outTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
			outTex.Apply();
			RenderTexture.active = prev;
			RenderTexture.ReleaseTemporary(rt);
			return outTex;
		}

		private static Texture2D SaveTexturePng(Texture2D tex, string folder, string nameNoExt,
		                                         bool isNormalMap = false)
		{
			if (tex == null) return null;
			string path = AssetDatabase.GenerateUniqueAssetPath(
			    Path.Combine(folder, nameNoExt + ".png").Replace('\\', '/'));
			System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
			Object.DestroyImmediate(tex);
			AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

			if (isNormalMap)
			{
				var importer = (TextureImporter)AssetImporter.GetAtPath(path);
				if (importer != null)
				{
					importer.textureType = TextureImporterType.NormalMap;
					importer.SaveAndReimport();
				}
			}
			return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
		}
	}
}
