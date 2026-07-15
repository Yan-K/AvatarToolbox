using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace YanK
{
	public partial class NonToonConverterTool
	{
		// =====================================================================
		// Styles
		// =====================================================================

		private GUIStyle warningBoxStyle;
		private GUIStyle bakeHelpStyle;
		private bool     extraStylesReady;

		private void EnsureExtraStyles()
		{
			if (extraStylesReady && warningBoxStyle != null) return;

			warningBoxStyle = new GUIStyle("box")
			{
				padding = new RectOffset(6, 6, 3, 3),
				margin  = new RectOffset(0, 0, 2, 2)
			};
			warningBoxStyle.normal.background = MakeTex(2, 2,
				EditorGUIUtility.isProSkin ? new Color(0.4f, 0.28f, 0.05f) : new Color(1f, 0.93f, 0.70f));

			bakeHelpStyle = new GUIStyle(EditorStyles.helpBox)
			{
				wordWrap  = true,
				fontSize  = 10,
				alignment = TextAnchor.MiddleLeft,
			};
			bakeHelpStyle.normal.textColor = EditorGUIUtility.isProSkin
				? new Color(0.9f, 0.75f, 0.3f) : new Color(0.5f, 0.35f, 0f);

			extraStylesReady = true;
		}

		// =====================================================================
		// Control panel
		// =====================================================================

		private void DrawControlPanel()
		{
			EnsureExtraStyles();
			GUILayout.Space(4);

			// --- Drop zone ---------------------------------------------------
			DrawGroupBox(() =>
			{
				DrawDropZone();
				GUILayout.Space(4);
				if (GUILayout.Button(L("ncRescan", "Rescan"), GUILayout.Height(26)))
					ScanMaterials();
			});

			GUILayout.Space(4);

			// --- Output + Advanced (merged, always visible) -----------------
			DrawGroupBox(() =>
			{
				// Output mode selector
				EditorGUILayout.LabelField(L("ncOutputFolder", "Output"), EditorStyles.boldLabel);
				int newModeIdx = GUILayout.Toolbar((int)outputMode,
					new[] { L("ncOutputModeOriginal", "Original (next to source)"),
					        L("ncOutputModeCustom",   "Custom folder") });
				var newMode = (OutputMode)newModeIdx;
				if (newMode != outputMode) SetOutputMode(newMode);

				if (outputMode == OutputMode.Custom)
				{
					GUILayout.Space(3);
					EditorGUILayout.BeginHorizontal();
					string newFolder = EditorGUILayout.TextField(customOutputFolder, GUILayout.ExpandWidth(true));
					if (newFolder != customOutputFolder) SetCustomOutputFolder(newFolder);
					if (GUILayout.Button(L("ncBrowse", "Browse…"), GUILayout.Width(64), GUILayout.Height(18)))
					{
						string picked = EditorUtility.OpenFolderPanel("Select Output Folder", "Assets", "");
						if (!string.IsNullOrEmpty(picked))
						{
							if (picked.StartsWith(Application.dataPath))
								picked = "Assets" + picked.Substring(Application.dataPath.Length);
							SetCustomOutputFolder(picked);
						}
					}
					EditorGUILayout.EndHorizontal();
				}

				DrawStyledSeparator();

				// Advanced Settings (foldout, default expanded)
				bool newFoldout = EditorGUILayout.Foldout(advancedFoldout,
					L("ncAdvancedSettings", "Advanced Settings"), true, EditorStyles.foldoutHeader);
				if (newFoldout != advancedFoldout) SetAdvancedFoldout(newFoldout);

				if (advancedFoldout)
				{
					GUILayout.Space(2);

					// Force Rendering Mode
					EditorGUILayout.BeginHorizontal();
					EditorGUILayout.LabelField(new GUIContent(
						L("ncForceRenderingMode", "Force Rendering Mode"),
						L("ncForceRenderingModeTooltip", "Override auto-detection from lilToon shader name")),
						GUILayout.Width(160));
					var newForce = (ForceMode)EditorGUILayout.EnumPopup(options.ForceRenderingMode, GUILayout.Width(110));
					if (newForce != options.ForceRenderingMode) SetForceRenderingMode(newForce);
					EditorGUILayout.EndHorizontal();
					GUILayout.Space(4);

					// Two columns: pipeline copy (left) | texture baking (right)
					float colW = Mathf.Max(140f, (position.width - 44f) * 0.5f);

					EditorGUILayout.BeginHorizontal();

					// -- Left column: pipeline copy --
					EditorGUILayout.BeginVertical(GUILayout.Width(colW));
					EditorGUILayout.LabelField(L("ncPipelineCopy", "Pipeline Copy"), EditorStyles.boldLabel);
					DrawAdvancedToggle("ncConvertFur", "Convert Fur",
						"Convert lilToon Fur materials to NonToonFur. OFF leaves Fur materials completely unconverted (they stay lilToon).",
						options.ConvertFur, SetConvertFur);
					DrawAdvancedToggle("ncCopyRenderQueue", "Copy Render Queue",
						"Copy renderQueue verbatim. OFF = mode-derived queue (Opaque=-1, Cutout=2450, Transparent=3000)",
						options.CopyRenderQueue, SetCopyRenderQueue);
					DrawAdvancedToggle("ncCopyStencil", "Copy Stencil",
						"Copy _StencilRef / _StencilComp / _StencilPass from source",
						options.CopyStencil, SetCopyStencil);
					DrawAdvancedToggle("ncCopyCull", "Copy Cull",
						"Copy _Cull from source (Back=2, Front=1, Off=0)",
						options.CopyCull, SetCopyCull);
					DrawAdvancedToggle("ncCopyZWrite", "Copy ZWrite",
						"Copy _ZWrite from source",
						options.CopyZWrite, SetCopyZWrite);
					DrawAdvancedToggle("ncCopyBlend", "Copy Blend",
						"Copy raw _SrcBlend / _DstBlend / _AlphaToMask, overriding mode-derived blend",
						options.CopyBlend, SetCopyBlend);
					EditorGUILayout.EndVertical();

					GUILayout.Space(6);

					// -- Right column: texture baking --
					EditorGUILayout.BeginVertical(GUILayout.Width(colW));
					EditorGUILayout.LabelField(L("ncBakeOptions", "Texture Baking"), EditorStyles.boldLabel);
					DrawAdvancedToggle("ncBakeMainTex",   "Bake Main Texture",
						"Composite Color + HSVG + 2nd/3rd layers into a single texture using lilToon's baker",
						options.BakeMainTex,   SetBakeMainTex);
					DrawAdvancedToggle("ncBakeAlphaMask", "Bake Alpha Mask",
						"Bake an enabled lilToon alpha mask with non-extreme transparency/cutoff into the base texture's alpha channel",
						options.BakeAlphaMask, SetBakeAlphaMask);
					DrawAdvancedToggle("ncBakeEmission",  "Bake Emission",
						"Port HDR color, alpha, Blend and texture strength through an editable SharedMask. Optimized for lilToon's Add mode; color remains grayscale.",
						options.BakeEmission,  SetBakeEmission);
					DrawAdvancedToggle("ncBakeNormalMap", "Bake Normal Map (tiling)",
						"Re-bake normal map when tiling/offset is non-default",
						options.BakeNormalMap, SetBakeNormalMap);
					DrawAdvancedToggle("ncBakeMasks",     "Create SharedMask",
						"Create a Shader Core .scmask whose RGBA slots link original textures or required composite bakes",
						options.BakeMasks,     SetBakeMasks);
					DrawAdvancedToggle("ncBakeShadow",    "Create Shadow Gradients",
						"Create an editable Shader Core .scgradients asset from lilToon shadow color",
						options.BakeShadow,    SetBakeShadow);
					EditorGUILayout.EndVertical();

					EditorGUILayout.EndHorizontal();

					bool anyBake = options.BakeMainTex || options.BakeAlphaMask || options.BakeShadow ||
					               options.BakeEmission || options.BakeNormalMap || options.BakeMasks;
					if (anyBake)
					{
						GUILayout.Space(3);
						GUILayout.Label(
							"⚠ " + L("ncBakeAtOwnRisk", "Baked results may not be accurate — use at your own risk."),
							bakeHelpStyle);
					}
				}
			});

			GUILayout.Space(4);
		}

		private void DrawAdvancedToggle(string locKey, string defText, string tooltip,
		                                 bool current, System.Action<bool> setter)
		{
			bool newVal = EditorGUILayout.ToggleLeft(
				new GUIContent(L(locKey, defText), L(locKey + "Tooltip", tooltip)), current);
			if (newVal != current) setter(newVal);
		}

		// =====================================================================
		// Drop zone (shows imported roots list)
		// =====================================================================

		private void DrawDropZone()
		{
			Rect dropRect;

			if (roots.Count == 0)
			{
				// Empty: show the hint box as the drop target
				var dropStyle = new GUIStyle(EditorStyles.helpBox)
				{
					alignment  = TextAnchor.MiddleCenter,
					fontSize   = 11,
					normal     = { textColor = EditorGUIUtility.isProSkin ? new Color(0.65f, 0.65f, 0.65f) : new Color(0.3f, 0.3f, 0.3f) }
				};
				dropRect = GUILayoutUtility.GetRect(0, 42, GUILayout.ExpandWidth(true));
				GUI.Box(dropRect, new GUIContent(
					L("ncDropZone", "Drop GameObjects or Materials here"),
					L("ncDropZoneTooltip", "Drop multiple objects — lilToon materials are detected automatically")),
					dropStyle);
			}
			else
			{
				// Populated: show the item list (scrollable, capped height); the whole area is the
				// drop target so users can keep dropping more items without the panel growing forever.
				EditorGUILayout.BeginVertical();

				var toRemove = new List<UnityEngine.Object>();

				// Size the scroll view to the actual row content height and only clamp once it
				// exceeds the max — EditorGUILayout.BeginScrollView otherwise always stretches to
				// GUILayout.MaxHeight regardless of how little content it holds.
				const float maxListHeight = 300f;
				float rowHeight  = EditorGUIUtility.singleLineHeight + 2f;
				float listHeight = Mathf.Min(roots.Count * rowHeight, maxListHeight);

				rootsScroll = EditorGUILayout.BeginScrollView(rootsScroll, GUILayout.Height(listHeight));
				foreach (var root in roots)
				{
					EditorGUILayout.BeginHorizontal();
					EditorGUI.BeginDisabledGroup(true);
					EditorGUILayout.ObjectField(root, typeof(UnityEngine.Object), true, GUILayout.ExpandWidth(true));
					EditorGUI.EndDisabledGroup();
					if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22), GUILayout.Height(18)))
						toRemove.Add(root);
					EditorGUILayout.EndHorizontal();
				}
				EditorGUILayout.EndScrollView();

				// Footer row: drop-more hint + Clear All
				EditorGUILayout.BeginHorizontal();
				EditorGUILayout.LabelField(L("ncDropMore", "Drop more here…"), dimLabelStyle);
				GUILayout.FlexibleSpace();
				if (GUILayout.Button(L("ncClearAll", "Clear All"), GUILayout.Width(68), GUILayout.Height(16)))
				{
					roots.Clear();
					conversionSlots.Clear();
					selectAll = false;
				}
				EditorGUILayout.EndHorizontal();

				EditorGUILayout.EndVertical();
				dropRect = GUILayoutUtility.GetLastRect();

				if (toRemove.Count > 0)
				{
					foreach (var r in toRemove) roots.Remove(r);
					ScanMaterials();
				}
			}

			// Drag-and-drop handler (works over the hint box or the item list)
			var ev = Event.current;
			if (ev == null) return;

			if ((ev.type == EventType.DragUpdated || ev.type == EventType.DragPerform) && dropRect.Contains(ev.mousePosition))
			{
				bool anyValid = DragAndDrop.objectReferences.Any(o => o is GameObject || o is Material);
				DragAndDrop.visualMode = anyValid ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;

				if (ev.type == EventType.DragPerform && anyValid)
				{
					DragAndDrop.AcceptDrag();
					bool changed = false;
					foreach (var obj in DragAndDrop.objectReferences)
					{
						if ((obj is GameObject || obj is Material) && !roots.Contains(obj))
						{
							roots.Add(obj);
							changed = true;
						}
					}
					if (changed) ScanMaterials();
					ev.Use();
				}
				else if (ev.type == EventType.DragUpdated) ev.Use();
			}
		}

		// =====================================================================
		// Material list
		// =====================================================================

		private void DrawMaterialList()
		{
			var filtered = GetFilteredSlots();

			// Select All + status
			EditorGUILayout.BeginHorizontal();
			bool newSelectAll = EditorGUILayout.ToggleLeft(
				L("ncSelectAll", "Select All"), selectAll, GUILayout.Width(90));
			if (newSelectAll != selectAll)
			{
				selectAll = newSelectAll;
				foreach (var s in conversionSlots) s.selected = selectAll;
			}
			GUILayout.FlexibleSpace();
			string statusText = string.IsNullOrEmpty(searchFilter)
				? string.Format(L("ncNMaterialsFound", "{0} material(s) found"), conversionSlots.Count)
				: string.Format(L("filterStatus",      "{0} of {1} shown"),       filtered.Count, conversionSlots.Count);
			EditorGUILayout.LabelField(statusText, statusLabelStyle);
			EditorGUILayout.EndHorizontal();

			searchFilter = DrawSearchField(searchFilter);
			GUILayout.Space(2);

			scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));
			foreach (var slot in filtered)
			{
				GUILayout.Space(ItemSpacing);
				EditorGUILayout.BeginVertical(cardStyle);
				DrawConversionSlot(slot);
				EditorGUILayout.EndVertical();
			}
			EditorGUILayout.EndScrollView();

			GUILayout.Space(4);
			DrawStyledSeparator();
			GUILayout.Space(4);

			int selectedCount = conversionSlots.Count(s => s.selected);
			using (new EditorGUI.DisabledScope(selectedCount == 0))
			{
				if (GUILayout.Button(
					new GUIContent(
						string.Format(L("ncConvertSelected", "Convert Selected ({0})"), selectedCount),
						L("ncConvertSelectedTooltip", "Create NonToon material assets and assign to renderers")),
					GUILayout.Height(36)))
				{
					ConvertSelected();
				}
			}
			GUILayout.Space(6);
		}

		// =====================================================================
		// Slot card
		// =====================================================================

		private void DrawConversionSlot(ConversionSlot slot)
		{
			// Row 1: checkbox + material field + target label
			EditorGUILayout.BeginHorizontal();
			bool newSel = EditorGUILayout.Toggle(slot.selected, GUILayout.Width(20));
			if (newSel != slot.selected) { slot.selected = newSel; UpdateSelectAll(); }
			EditorGUILayout.ObjectField(slot.material, typeof(Material), false, GUILayout.ExpandWidth(true));
			bool   isFurMat      = IsFurMaterial(slot.material);
			bool   willBeSkipped = isFurMat && !options.ConvertFur;
			string targetLabel   = willBeSkipped ? L("ncTargetSkipped", "⏭ Skipped (Fur)") : (isFurMat ? "→ NonToonFur" : "→ NonToon");
			EditorGUILayout.LabelField(targetLabel, dimLabelStyle, GUILayout.Width(100));
			EditorGUILayout.EndHorizontal();

			// Row 2: source shader dimmed
			if (slot.material?.shader != null)
			{
				EditorGUI.indentLevel++;
				EditorGUILayout.LabelField(slot.material.shader.name, dimLabelStyle);
				EditorGUI.indentLevel--;
			}

			// Row 3: renderer foldout (only when there are refs)
			if (slot.refs.Count > 0)
			{
				bool newFoldout = EditorGUILayout.Foldout(slot.foldout,
					string.Format(L("usedBy", "Used by {0} renderer(s)"), slot.RendererCount), true);
				if (newFoldout != slot.foldout) slot.foldout = newFoldout;
				if (slot.foldout)
				{
					EditorGUI.indentLevel++;
					foreach (var r in slot.Renderers)
						if (r != null) EditorGUILayout.ObjectField(r, typeof(Renderer), true);
					EditorGUI.indentLevel--;
				}
			}

			// Row 4: unsupported warnings
			if (slot.warnings?.Count > 0)
			{
				GUILayout.Space(3);
				EditorGUILayout.BeginVertical(warningBoxStyle);
				EditorGUILayout.LabelField(
					"⚠ " + L("ncUnsupportedFeatures", "Features that will not transfer:"),
					EditorStyles.miniLabel);
				foreach (var w in slot.warnings)
					EditorGUILayout.LabelField("  • " + L(w.locKey, w.defaultText), dimLabelStyle);
				EditorGUILayout.EndVertical();
			}
		}

		// =====================================================================
		// Empty state
		// =====================================================================

		private void DrawEmptyState()
		{
			DrawCenteredMessage(
				roots.Count == 0
					? L("ncEmptyHint", "Drop GameObjects or Materials into the field above to begin.")
					: L("ncNoLilToonFound", "No lilToon materials found. Drop GameObjects or Materials above."),
				"d_Material Icon");
		}
	}
}
