using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace YanK
{
	public partial class NonToonConverterTool
	{
		// =====================================================================
		// Data types
		// =====================================================================

		private class RendererRef
		{
			public Renderer renderer;
			public int      materialIndex;
		}

		private class ConversionSlot
		{
			public Material               material;
			public bool                   selected  = true;
			public bool                   foldout;
			public List<RendererRef>      refs      = new List<RendererRef>();
			public List<UnsupportedEntry> warnings;

			public IEnumerable<Renderer> Renderers    => refs.Select(r => r.renderer).Distinct();
			public int                   RendererCount => refs.Select(r => r.renderer).Distinct().Count();
			/// <summary>True when this slot came from a dropped Material (no renderer refs).</summary>
			public bool MaterialOnly => refs.Count == 0;
		}

		/// <summary>Tracks a single renderer material slot that uses lilToon Fake Shadow.</summary>
		private class FakeShadowSlot
		{
			public Renderer renderer;
			public int      materialIndex;
			public string   ObjectName => renderer != null ? renderer.gameObject.name : "(null)";
		}

		// =====================================================================
		// State
		// =====================================================================

		private readonly List<ConversionSlot>  conversionSlots  = new List<ConversionSlot>();
		private readonly List<FakeShadowSlot>  fakeShadowSlots  = new List<FakeShadowSlot>();

		// =====================================================================
		// Scanning — always includes inactive renderers
		// =====================================================================

		private void ScanMaterials()
		{
			conversionSlots.Clear();
			selectAll = false;
			if (roots.Count == 0) return;

			var seen = new Dictionary<Material, ConversionSlot>();

			foreach (var root in roots)
			{
				if (root == null) continue;

				if (root is GameObject go)
				{
					// includeInactive = true always
					foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
					{
						var mats = renderer.sharedMaterials;
						for (int i = 0; i < mats.Length; i++)
						{
							var mat = mats[i];
							if (mat == null || !IsLilToon(mat)) continue;
							if (!seen.TryGetValue(mat, out var slot))
							{
								slot = new ConversionSlot { material = mat };
								seen[mat] = slot;
								conversionSlots.Add(slot);
							}
							slot.refs.Add(new RendererRef { renderer = renderer, materialIndex = i });
						}
					}
				}
				else if (root is Material rootMat && IsLilToon(rootMat))
				{
					if (!seen.ContainsKey(rootMat))
					{
						var slot = new ConversionSlot { material = rootMat };
						seen[rootMat] = slot;
						conversionSlots.Add(slot);
					}
				}
			}

			foreach (var slot in conversionSlots)
				slot.warnings = GetUnsupportedFeatures(slot.material, options.ConvertFur && IsFurMaterial(slot.material));

			// Scan all renderer slots for lilToon Fake Shadow (different shader, not caught above)
			fakeShadowSlots.Clear();
			foreach (var root in roots)
			{
				if (!(root is GameObject go)) continue;
				foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
				{
					var mats = renderer.sharedMaterials;
					for (int i = 0; i < mats.Length; i++)
						if (IsFakeShadow(mats[i]))
							fakeShadowSlots.Add(new FakeShadowSlot { renderer = renderer, materialIndex = i });
				}
			}
		}

		private static bool IsFakeShadow(Material m) =>
			m?.shader != null &&
			m.shader.name.IndexOf("fakeshadow", System.StringComparison.OrdinalIgnoreCase) >= 0;

		private void RemoveFakeShadowSlots()
		{
			var grouped = fakeShadowSlots
				.Where(s => s.renderer != null)
				.GroupBy(s => s.renderer)
				.ToList();

			foreach (var group in grouped)
			{
				var renderer = group.Key;
				Undo.RecordObject(renderer, "Remove Fake Shadow Slot");
				var mats = new System.Collections.Generic.List<Material>(renderer.sharedMaterials);
				// Remove highest indices first so lower indices stay valid
				foreach (var s in group.OrderByDescending(x => x.materialIndex))
					if (s.materialIndex < mats.Count) mats.RemoveAt(s.materialIndex);
				renderer.sharedMaterials = mats.ToArray();
				EditorUtility.SetDirty(renderer);
			}

			fakeShadowSlots.Clear();
			ScanMaterials();
			Repaint();
		}

		// =====================================================================
		// Helpers
		// =====================================================================

		private void UpdateSelectAll() =>
			selectAll = conversionSlots.Count > 0 && conversionSlots.All(s => s.selected);

		private List<ConversionSlot> GetFilteredSlots()
		{
			if (string.IsNullOrEmpty(searchFilter)) return conversionSlots;
			string f = searchFilter.ToLowerInvariant();
			return conversionSlots.Where(s =>
				s.material != null &&
				(s.material.name.ToLowerInvariant().Contains(f) ||
				 s.material.shader.name.ToLowerInvariant().Contains(f))
			).ToList();
		}
	}
}
