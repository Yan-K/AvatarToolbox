using UnityEngine;
using System.Collections.Generic;

namespace YanK
{
	public partial class NonToonConverterTool
	{
		// =====================================================================
		// Module property prefix constants
		// =====================================================================

		private const string P_Lighten  = "_jp_lilxyzw_nontoon_lighten_";
		private const string P_RimLight = "_jp_lilxyzw_nontoon_rimlight_";
		private const string P_MatCaps  = "_jp_lilxyzw_nontoon_matcaps_";
		private const string P_DistFade = "_jp_lilxyzw_nontoon_distancefade_";
		private const string P_Shade    = "_jp_lilxyzw_nontoon_shade_";
		private const string P_RimShade = "_jp_lilxyzw_nontoon_rimshade_";
		private const string P_Specular = "_jp_lilxyzw_nontoon_specular_";
		private const string P_Details  = "_jp_lilxyzw_nontoon_details_";
		private const string P_Lighten2 = "_jp_lilxyzw_nontoon_lighten_"; // same module

		// =====================================================================
		// Property mapping table
		// =====================================================================

		private enum PropKind { Texture, Color, Float, Vector }

		private struct PropMapping
		{
			public string   sourceProp;
			public string   targetProp;
			public PropKind kind;
			public System.Func<float, float> floatTransform;
			public bool furOnly;
			/// <summary>Optional gate — mapping applies only when this returns true. Null = always.</summary>
			public System.Func<Material, bool> enableCheck;
		}

		/// <summary>Core mappings — visual properties, excluding outline / rim / matcap-color (handled specially).</summary>
		private static readonly PropMapping[] CoreMappings =
		{
			// --- Base texture (always; color/tiling folded in by baking) ---
			new PropMapping { sourceProp = "_MainTex",   targetProp = "_BaseTexture",  kind = PropKind.Texture },

			// --- Normal map texture (gated on _UseBumpMap; tiling handled by bake) ---
			new PropMapping { sourceProp = "_BumpMap",   targetProp = "_NormalMap",    kind = PropKind.Texture,
			                  enableCheck = m => IsFeatureActive(m, "_UseBumpMap") },
			new PropMapping { sourceProp = "_BumpScale", targetProp = "_NormalScale",  kind = PropKind.Float,
			                  enableCheck = m => IsFeatureActive(m, "_UseBumpMap") },

			// --- Alpha cutoff ---
			new PropMapping { sourceProp = "_Cutoff",    targetProp = "_Cutoff",       kind = PropKind.Float   },

			// --- Roughness (inverted smoothness; only when reflection used) ---
			new PropMapping
			{
				sourceProp     = "_Smoothness",
				targetProp     = "_Roughness",
				kind           = PropKind.Float,
				floatTransform = v => Mathf.Clamp(1f - v, 0.002f, 1f),
				enableCheck    = m => IsFeatureActive(m, "_UseReflection")
			},

			// --- MatCap textures (gated; colors + module-enable handled specially) ---
			new PropMapping { sourceProp = "_MatCapTex", targetProp = P_MatCaps + "MatCapMultiply", kind = PropKind.Texture,
			                  enableCheck = m => IsFeatureActive(m, "_UseMatCap") },
			new PropMapping { sourceProp = "_MatCap2ndTex", targetProp = P_MatCaps + "MatCapAdd",   kind = PropKind.Texture,
			                  enableCheck = m => IsFeatureActive(m, "_UseMatCap2nd") },
		};

		/// <summary>Fur-specific mappings.</summary>
		private static readonly PropMapping[] FurMappings =
		{
			new PropMapping { sourceProp = "_FurNoiseMask",       targetProp = "_FurNoiseMask",       kind = PropKind.Texture, furOnly = true },
			new PropMapping { sourceProp = "_FurVector",          targetProp = "_FurVector",          kind = PropKind.Vector,  furOnly = true },
			new PropMapping { sourceProp = "_FurRimColor",        targetProp = "_FurRimColor",        kind = PropKind.Color,   furOnly = true },
			new PropMapping { sourceProp = "_FurRimFresnelPower", targetProp = "_FurRimFresnelPower", kind = PropKind.Float,   furOnly = true },
			new PropMapping { sourceProp = "_FurRimAntiLight",    targetProp = "_FurRimAntiLight",    kind = PropKind.Float,   furOnly = true },
			new PropMapping
			{
				sourceProp     = "_FurLayerNum",
				targetProp     = "_FurSubdivision",
				kind           = PropKind.Float,
				floatTransform = v => Mathf.Clamp(Mathf.Round(v), 1f, 3f),
				furOnly        = true
			},
		};

		// =====================================================================
		// Unsupported-feature detection
		// =====================================================================

		private struct UnsupportedEntry
		{
			public string locKey;
			public string defaultText;
		}

		private static List<UnsupportedEntry> GetUnsupportedFeatures(Material src, bool isFurTarget)
		{
			var list = new List<UnsupportedEntry>();

			// Main color tint — NonToon has no base tint
			if (src.HasProperty("_Color"))
			{
				var c = src.GetColor("_Color");
				if (c.r < 0.99f || c.g < 0.99f || c.b < 0.99f || c.a < 0.99f)
					list.Add(new UnsupportedEntry { locKey = "ncFeatColorTint", defaultText = "Main color tint (_Color)" });
			}

			if (HasActiveVector(src, "_MainTex_ScrollRotate"))
				list.Add(new UnsupportedEntry { locKey = "ncFeatUVScroll",  defaultText = "UV scroll / rotate" });
			if (IsFeatureActive(src, "_UseMain2ndTex"))
				list.Add(new UnsupportedEntry { locKey = "ncFeatMain2nd",   defaultText = "Secondary texture layer" });
			if (IsFeatureActive(src, "_UseMain3rdTex"))
				list.Add(new UnsupportedEntry { locKey = "ncFeatMain3rd",   defaultText = "Third texture layer" });
			if (IsFeatureActive(src, "_UseDissolve"))
				list.Add(new UnsupportedEntry { locKey = "ncFeatDissolve",  defaultText = "Dissolve" });
			if (IsFeatureActive(src, "_UseRimShade"))
				list.Add(new UnsupportedEntry { locKey = "ncFeatRimShade",  defaultText = "Rim shade" });
			if (IsFeatureActive(src, "_UseGlitter"))
				list.Add(new UnsupportedEntry { locKey = "ncFeatGlitter",   defaultText = "Glitter" });
			if (IsFeatureActive(src, "_UseParallax"))
				list.Add(new UnsupportedEntry { locKey = "ncFeatParallax",  defaultText = "Parallax / height map" });
			if (IsFeatureActive(src, "_UseAnisotropy"))
				list.Add(new UnsupportedEntry { locKey = "ncFeatAnisotropy", defaultText = "Anisotropy" });
			if (IsFeatureActive(src, "_UseAudioLink"))
				list.Add(new UnsupportedEntry { locKey = "ncFeatAudioLink", defaultText = "AudioLink" });
			if (IsFeatureActive(src, "_UseReflection"))
				list.Add(new UnsupportedEntry { locKey = "ncFeatReflection", defaultText = "Reflection / metallic" });

			string shaderName = src.shader.name;
			if (shaderName.IndexOf("gem",       System.StringComparison.OrdinalIgnoreCase) >= 0 ||
			    shaderName.IndexOf("refraction", System.StringComparison.OrdinalIgnoreCase) >= 0)
				list.Add(new UnsupportedEntry { locKey = "ncFeatGemRefraction", defaultText = "Gem / refraction" });

			if (isFurTarget)
			{
				bool hasAdvancedFur =
					HasNonZeroFloat(src, "_FurGravity")      || HasNonZeroFloat(src, "_FurVectorScale") ||
					HasNonNullTex  (src, "_FurLengthMask")   || HasNonNullTex  (src, "_FurVectorTex")   ||
					HasNonZeroFloat(src, "_FurTouchStrength") || HasNonZeroFloat(src, "_FurRandomize")   ||
					HasNonZeroFloat(src, "_FurAO")            || HasNonZeroFloat(src, "_FurRootOffset")  ||
					HasNonNullTex  (src, "_FurMask");
				if (hasAdvancedFur)
					list.Add(new UnsupportedEntry { locKey = "ncFeatFurAdvanced", defaultText = "Advanced fur settings" });
			}

			return list;
		}

		// =====================================================================
		// Source material helpers
		// =====================================================================

		private static bool IsLilToon(Material m) =>
			m != null && m.shader != null &&
			m.shader.name.IndexOf("liltoon", System.StringComparison.OrdinalIgnoreCase) >= 0;

		private static bool IsFurMaterial(Material m)
		{
			if (m == null) return false;
			string name = m.shader.name;
			if (name.IndexOf("fur", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
			if (name.IndexOf("multi", System.StringComparison.OrdinalIgnoreCase) >= 0 &&
			    m.HasProperty("_TransparentMode"))
			{
				int tm = (int)m.GetFloat("_TransparentMode");
				return tm == 4 || tm == 5;
			}
			return false;
		}

		private static bool HasOutlineEnabled(Material m)
		{
			if (m == null) return false;
			// outline variant shader
			if (m.shader.name.IndexOf("outline", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
			// multi shader with _UseOutline toggle
			if (m.HasProperty("_UseOutline") && m.GetFloat("_UseOutline") > 0.5f) return true;
			return false;
		}

		private static int DetectRenderingMode(Material src, ForceMode force)
		{
			if (force == ForceMode.Opaque)      return 0;
			if (force == ForceMode.Cutout)      return 1;
			if (force == ForceMode.Transparent) return 2;

			string name = src.shader.name;
			bool isMulti = name.IndexOf("multi", System.StringComparison.OrdinalIgnoreCase) >= 0;

			if (isMulti && src.HasProperty("_TransparentMode"))
			{
				switch ((int)src.GetFloat("_TransparentMode"))
				{
					case 1: case 5: return 1;
					case 2: case 4: return 2;
					default:        return 0;
				}
			}

			if (name.IndexOf("cutout", System.StringComparison.OrdinalIgnoreCase) >= 0) return 1;
			if (name.IndexOf("trans",  System.StringComparison.OrdinalIgnoreCase) >= 0) return 2;
			return 0;
		}

		// ----- small predicates -----

		private static bool IsFeatureActive(Material m, string prop) =>
			m.HasProperty(prop) && m.GetFloat(prop) > 0.5f;

		private static bool HasActiveVector(Material m, string prop)
		{
			if (!m.HasProperty(prop)) return false;
			var v = m.GetVector(prop);
			return v.x != 0f || v.y != 0f || v.z != 0f || v.w != 0f;
		}

		private static bool HasNonZeroFloat(Material m, string prop) =>
			m.HasProperty(prop) && m.GetFloat(prop) != 0f;

		private static bool HasNonNullTex(Material m, string prop) =>
			m.HasProperty(prop) && m.GetTexture(prop) != null;
	}
}
