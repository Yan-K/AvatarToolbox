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

		// =====================================================================
		// State
		// =====================================================================

		private readonly List<ConversionSlot> conversionSlots = new List<ConversionSlot>();

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
				slot.warnings = GetUnsupportedFeatures(slot.material, IsFurMaterial(slot.material));
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
