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
		}

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
			// No _EmissionColor property → no alpha-as-strength semantics; treat as active.
			if (!src.HasProperty("_EmissionColor")) return true;
			return src.GetColor("_EmissionColor").a > EmissionAlphaThreshold;
		}

		private static List<MaskCandidate> CollectMaskCandidates(Material src, ConverterOptions opt)
		{
			var list = new List<MaskCandidate>();
			if (!opt.BakeMasks) return list;

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
				});
			}

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
				});
			}
			if (mcUse2 && HasNonNullTex(src, "_MatCap2ndBlendMask"))
			{
				list.Add(new MaskCandidate
				{
					featureName     = mcMul2 ? "MatCap Multiply Mask" : "MatCap Add Mask",
					maskChannelProp = P_MatCaps + (mcMul2 ? "MatCapMultiplyMaskChannel" : "MatCapAddMaskChannel"),
					srcMask         = src.GetTexture("_MatCap2ndBlendMask"),
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
			if (opt.BakeMasks && resolvedMasks != null && resolvedMasks.Count > 0)
			{
				PackSharedMask(src, dst, opt, resolvedMasks, folder, baseName);
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

			const int W = 128, H = 4;
			var gradArr = new Texture2DArray(W, H, 1, TextureFormat.RGBA32, false, true);
			var pixels  = new Color[W * H];
			for (int y = 0; y < H; y++)
				for (int x = 0; x < W; x++)
					pixels[y * W + x] = Color.Lerp(shadowColor, Color.white, x / (float)(W - 1));
			gradArr.SetPixels(pixels, 0, 0);
			gradArr.Apply();
			gradArr.filterMode = FilterMode.Bilinear;
			gradArr.wrapMode   = TextureWrapMode.Clamp;

			string path = AssetDatabase.GenerateUniqueAssetPath(
			    Path.Combine(folder, baseName + "_Shade_NTBake.asset").Replace('\\', '/'));
			AssetDatabase.CreateAsset(gradArr, path);
			return AssetDatabase.LoadAssetAtPath<Texture2DArray>(path);
		}

		// =====================================================================
		// SharedMask packing
		// =====================================================================

		private static void PackSharedMask(Material src, Material dst, ConverterOptions opt,
		                                    List<MaskCandidate> candidates,
		                                    string folder, string baseName)
		{
			// Determine canvas size
			int w = 4, h = 4;
			foreach (var c in candidates)
			{
				if (c.srcMask != null)
				{
					w = Mathf.Max(w, c.srcMask.width);
					h = Mathf.Max(h, c.srcMask.height);
				}
			}

			var outTex  = new Texture2D(w, h, TextureFormat.RGBA32, false);
			var outPx   = new Color[w * h];
			// Default white (channel = 1 → full effect)
			for (int i = 0; i < outPx.Length; i++) outPx[i] = Color.white;

			// Pack emission separately (needs luma bake)
			for (int ch = 0; ch < Mathf.Min(candidates.Count, 4); ch++)
			{
				var cand = candidates[ch];

				Color[] gray;
				if (cand.isEmission)
					gray = BakeEmissionToGrayscale(src, w, h, cand.srcMask);
				else
					gray = SampleMaskGrayscale(cand.srcMask, w, h);

				if (gray == null) continue;

				for (int i = 0; i < outPx.Length; i++)
				{
					float v = gray[i].r;
					switch (ch)
					{
						case 0: outPx[i].r = v; break;
						case 1: outPx[i].g = v; break;
						case 2: outPx[i].b = v; break;
						case 3: outPx[i].a = v; break;
					}
				}

				// Assign channel index to NonToon feature
				SetIntIfExists(dst, cand.maskChannelProp, ch);

				// Activate Lighten / LightBoostAsEmission for emission
				if (cand.isEmission)
				{
					SetIntIfExists(dst, P_Lighten + "LightBoostAsEmission", 1);
					SetFloatIfExists(dst, P_Lighten + "LightBoost", 1f);
					Debug.Log("[YNC] Emission ported to Lighten (grayscale only — color information is lost).");
				}
			}

			outTex.SetPixels(outPx);
			outTex.Apply();

			var sharedMask = SaveTexturePng(outTex, folder, baseName + "_Mask_NTBake");
			if (sharedMask != null && dst.HasProperty("_SharedMask"))
				dst.SetTexture("_SharedMask", sharedMask);
		}

		/// <summary>
		/// Bakes lilToon emission (map × color, with alpha as strength — B1) into a grayscale buffer,
		/// then multiplies in <paramref name="blendMask"/> if the source used `_EmissionBlendMask`
		/// (B2) — that mask cuts OUT parts of the emission map/color; it is a SEPARATE texture from
		/// `_EmissionMap`.
		/// </summary>
		private static Color[] BakeEmissionToGrayscale(Material src, int targetW, int targetH, Texture blendMask)
		{
			var emTex = src.HasProperty("_EmissionMap") ? src.GetTexture("_EmissionMap") : null;
			Color emColor = src.HasProperty("_EmissionColor") ? src.GetColor("_EmissionColor") : Color.white;
			float luma  = 0.299f * emColor.r + 0.587f * emColor.g + 0.114f * emColor.b;
			// B1: alpha is the emission strength in lilToon — fold it into the bias.
			float alpha = src.HasProperty("_EmissionColor") ? Mathf.Clamp01(emColor.a) : 1f;
			float bias  = luma * alpha;

			Color[] basePixels;
			if (emTex != null)
			{
				basePixels = SampleMaskGrayscale(emTex, targetW, targetH, useLuma: true);
				for (int i = 0; i < basePixels.Length; i++)
				{
					float v = basePixels[i].r * bias;
					basePixels[i] = new Color(v, v, v, 1f);
				}
			}
			else
			{
				basePixels = new Color[targetW * targetH];
				for (int i = 0; i < basePixels.Length; i++)
					basePixels[i] = new Color(bias, bias, bias, 1f);
			}

			// B2: multiply in the separate emission blend mask, if any.
			if (blendMask != null)
			{
				var maskPixels = SampleMaskGrayscale(blendMask, targetW, targetH);
				if (maskPixels != null)
				{
					for (int i = 0; i < basePixels.Length; i++)
					{
						float v = basePixels[i].r * maskPixels[i].r;
						basePixels[i] = new Color(v, v, v, 1f);
					}
				}
			}

			return basePixels;
		}

		private static Color[] SampleMaskGrayscale(Texture src, int targetW, int targetH, bool useLuma = false)
		{
			if (src == null) return null;
			var rt   = RenderTexture.GetTemporary(targetW, targetH, 0, RenderTextureFormat.ARGB32);
			var prev = RenderTexture.active;
			Graphics.Blit(src, rt);
			RenderTexture.active = rt;
			var tmp = new Texture2D(targetW, targetH, TextureFormat.RGBA32, false);
			tmp.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
			tmp.Apply();
			RenderTexture.active = prev;
			RenderTexture.ReleaseTemporary(rt);

			var px  = tmp.GetPixels();
			Object.DestroyImmediate(tmp);

			if (!useLuma) return px; // caller reads .r channel
			// Convert to grayscale (luma)
			for (int i = 0; i < px.Length; i++)
			{
				float v = 0.299f * px[i].r + 0.587f * px[i].g + 0.114f * px[i].b;
				px[i] = new Color(v, v, v, 1f);
			}
			return px;
		}

		/// <summary>
		/// Bake a blurred version of <paramref name="src"/> using multi-pass downsample/upsample
		/// (cheap Gaussian approximation matching lilToon's Lod/Blur parameter).
		/// <paramref name="lod"/> is lilToon's _MatCapLod range 0–10.
		/// </summary>
		private static Texture2D BakeBlurredTexture(Texture2D src, float lod, string folder, string nameNoExt)
		{
			if (src == null || lod <= 0.05f) return src;

			var full = LoadFullResReadable(src);
			int w = full.width, h = full.height;

			// Map LOD 0-10 to blur passes 1-5 (each pass halves then doubles = 1 blur level).
			int passes = Mathf.Clamp(Mathf.RoundToInt(lod * 0.5f), 1, 5);

			var current = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
			Graphics.Blit(full, current);

			for (int i = 0; i < passes; i++)
			{
				int sw = Mathf.Max(1, w >> (i + 1)), sh = Mathf.Max(1, h >> (i + 1));
				var smaller = RenderTexture.GetTemporary(sw, sh, 0, RenderTextureFormat.ARGB32);
				Graphics.Blit(current, smaller);          // downsample → blurs via bilinear
				RenderTexture.ReleaseTemporary(current);
				current = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
				Graphics.Blit(smaller, current);           // upsample → soft blur
				RenderTexture.ReleaseTemporary(smaller);
			}

			var prev   = RenderTexture.active;
			RenderTexture.active = current;
			var outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
			outTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
			outTex.Apply();
			RenderTexture.active = prev;
			RenderTexture.ReleaseTemporary(current);

			if (full != null && full != src) Object.DestroyImmediate(full);

			var saved = SaveTexturePng(outTex, folder, nameNoExt + "_Blur_NTBake");
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
