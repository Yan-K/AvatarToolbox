using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace YanK
{
	public partial class BlendshapeEditorTool
	{
		// --- Top-level batch / global controls ---

		private void DrawGlobalControls()
		{
			DrawGroupBox(() =>
			{
				EditorGUILayout.BeginHorizontal();
				resetFoldout = EditorGUILayout.Foldout(resetFoldout, L("bseBatchLabel", "Batch"), true, EditorStyles.foldoutHeader);
				GUI.enabled = !inPreview;
				if (GUILayout.Button(L("bseApplyValues", "Apply"), GUILayout.Height(20), GUILayout.ExpandWidth(true)))
					ApplyCurrentAsDefault();
				if (GUILayout.Button(L("bseCancelValues", "Cancel"), GUILayout.Height(20), GUILayout.ExpandWidth(true)))
					CancelAllToDefault();
				GUI.enabled = true;
				EditorGUILayout.EndHorizontal();

				if (!resetFoldout) return;

				GUILayout.Space(2);
				GUI.enabled = !inPreview;

				// ── Reset ── (All buttons | gap | Selected buttons)
				EditorGUILayout.BeginHorizontal();
				EditorGUILayout.LabelField(L("bseResetLabel", "Reset"), GUILayout.Width(42));
				if (GUILayout.Button(L("bseResetAllZero", "All → 0"), GUILayout.Height(22)))
					ConfirmResetAllZero();
				if (GUILayout.Button(L("bseResetAllDefault", "All → Default"), GUILayout.Height(22)))
					ConfirmResetDefault();
				GUILayout.Space(10);
				if (GUILayout.Button(L("bseResetSelZero", "Sel → 0"), GUILayout.Height(22)))
					ResetSelectedToZero();
				if (GUILayout.Button(L("bseResetSelDefault", "Sel → Default"), GUILayout.Height(22)))
					ResetSelectedToDefault();
				EditorGUILayout.EndHorizontal();

				GUILayout.Space(2);

				// ── Batch slider: real-time preview on all selected blendshapes ──
				EditorGUILayout.BeginHorizontal();
				int selCount = CountSelected();
				EditorGUILayout.LabelField(
					string.Format(L("bseBatchSliderCount", "Batch Value ({0} selected)"), selCount),
					GUILayout.Width(140));

				bool canDrag = !inPreview && selCount > 0 && targetRenderer != null;
				GUI.enabled = canDrag;

				EditorGUI.BeginChangeCheck();
				float newVal = EditorGUILayout.Slider(globalBatchValue, 0f, 100f);
				if (EditorGUI.EndChangeCheck())
				{
					// Applied like an Inspector edit; Unity merges one drag into a single Undo step.
					globalBatchValue = newVal;
					WriteWeights(s => s.selected, s => newVal, "YBE: Batch Value");
				}

				// Apply button: commits the current batch value and clears the checkbox selection
				// so the next round of batch editing starts fresh (a common user trap).
				GUI.enabled = !inPreview && selCount > 0 && targetRenderer != null;
				if (GUILayout.Button(L("bseApplyBatch", "Apply"), GUILayout.Width(60), GUILayout.Height(18)))
				{
					ApplyBatchAndClearSelection();
				}
				GUI.enabled = true;
				EditorGUILayout.EndHorizontal();
			});
		}

		private void ApplyBatchAndClearSelection()
		{
			if (targetRenderer == null) return;
			// Values are normally written while dragging; this also covers a value typed into the field.
			WriteWeights(s => s.selected, s => globalBatchValue, "YBE: Batch Value Apply");
			foreach (var s in slots)
			{
				if (!s.isSeparator) s.selected = false;
			}
			globalBatchValue = 0f;
			shiftAnchorDisplayIndex = -1;
		}

		// --- Group Tab Bar (wraps to multiple rows, shows full text) ---

		private void DrawGroupTabBar()
		{
			if (groupNames.Count <= 1) return;

			var style = new GUIStyle(EditorStyles.miniButton) { fixedHeight = 22 };
			float availableWidth = EditorGUIUtility.currentViewWidth - 24f;
			const float gap = 2f;

			EditorGUILayout.BeginVertical();
			EditorGUILayout.BeginHorizontal();
			float rowWidth = 0f;

			for (int i = 0; i < groupNames.Count; i++)
			{
				string label = GetGroupDisplayName(groupNames[i]);
				var content = new GUIContent(label);
				float btnWidth = style.CalcSize(content).x + 12f;
				if (btnWidth > availableWidth) btnWidth = availableWidth;

				if (rowWidth > 0f && rowWidth + btnWidth + gap > availableWidth)
				{
					EditorGUILayout.EndHorizontal();
					EditorGUILayout.BeginHorizontal();
					rowWidth = 0f;
				}

				bool isSelected = (i == currentGroupIndex);
				Color prevBg = GUI.backgroundColor;
				if (isSelected)
					GUI.backgroundColor = EditorGUIUtility.isProSkin
						? new Color(0.4f, 0.6f, 1f, 1f)
						: new Color(0.35f, 0.55f, 0.95f, 1f);

				if (GUILayout.Button(content, style, GUILayout.Width(btnWidth)))
				{
					if (currentGroupIndex != i)
					{
						currentGroupIndex = i;
						shiftAnchorDisplayIndex = -1;
					}
				}

				GUI.backgroundColor = prevBg;
				rowWidth += btnWidth + gap;
			}

			EditorGUILayout.EndHorizontal();
			EditorGUILayout.EndVertical();
			GUILayout.Space(2);
		}

		// --- Search + Status ---

		private void DrawSearchAndStatus()
		{
			string prev = searchFilter;
			searchFilter = DrawSearchField(searchFilter);
			if (prev != searchFilter)
			{
				shiftAnchorDisplayIndex = -1;
			}

			var displayed = GetDisplayedSlots();
			int displayedSlotCount = CountSlotsInList(displayed);
			int total = CountNonSeparators();
			int sel = CountSelected();

			EditorGUILayout.BeginHorizontal();

			// Select All / None buttons (replace single toggle so users have explicit control).
			if (GUILayout.Button(L("bseSelectAll", "Select All"), GUILayout.Width(90)))
			{
				foreach (var s in displayed)
				{
					if (s.isSeparator) continue;
					s.selected = true;
				}
			}
			if (GUILayout.Button(L("bseSelectNone", "Select None"), GUILayout.Width(90)))
			{
				foreach (var s in displayed)
				{
					if (s.isSeparator) continue;
					s.selected = false;
				}
				shiftAnchorDisplayIndex = -1;
			}

			GUILayout.FlexibleSpace();

			if (!string.IsNullOrEmpty(searchFilter) || currentGroupIndex > 0)
				EditorGUILayout.LabelField(string.Format(L("bseShownOfTotal", "{0} / {1} shown  |  {2} selected"), displayedSlotCount, total, sel), statusLabelStyle);
			else
				EditorGUILayout.LabelField(string.Format(L("bseTotalSelected", "{0} blendshapes  |  {1} selected"), total, sel), statusLabelStyle);

			EditorGUILayout.EndHorizontal();
		}

		// --- Main List ---

		private void DrawSlotList()
		{
			var displayed = GetDisplayedSlots();
			if (displayed.Count == 0)
			{
				EditorGUILayout.HelpBox(L("bseNoneMatched", "No blendshapes match the current filter."), MessageType.Info);
				return;
			}

			scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));

			for (int i = 0; i < displayed.Count; i++)
			{
				var slot = displayed[i];
				if (slot.isSeparator) DrawGroupHeaderRow(slot);
				else DrawSlotRow(slot, displayed, i);
			}

			EditorGUILayout.EndScrollView();
		}

		private void DrawGroupHeaderRow(BlendshapeSlot slot)
		{
			GUILayout.Space(4);
			string label = string.IsNullOrEmpty(slot.groupName) ? slot.name : slot.groupName;
			var bgStyle = new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(6, 6, 4, 4) };
			EditorGUILayout.BeginVertical(bgStyle);
			var labelStyle = new GUIStyle(EditorStyles.boldLabel)
			{
				alignment = TextAnchor.MiddleCenter,
				fontSize = 12,
			};
			EditorGUILayout.LabelField(label, labelStyle);
			EditorGUILayout.EndVertical();
			GUILayout.Space(2);
		}

		private void DrawSlotRow(BlendshapeSlot slot, List<BlendshapeSlot> displayed, int displayIndex)
		{
			bool isModified = !inPreview && IsModified(slot, slot.current);
			var style = isModified ? modifiedCardStyle : cardStyle;
			EditorGUILayout.BeginHorizontal(style);

			bool prev = slot.selected;
			bool newSelected = EditorGUILayout.Toggle(prev, GUILayout.Width(18));
			if (newSelected != prev)
			{
				bool shiftHeld = Event.current != null && Event.current.shift;
				if (shiftHeld && shiftAnchorDisplayIndex >= 0 && shiftAnchorDisplayIndex < displayed.Count)
				{
					int lo = Mathf.Min(shiftAnchorDisplayIndex, displayIndex);
					int hi = Mathf.Max(shiftAnchorDisplayIndex, displayIndex);
					for (int k = lo; k <= hi; k++)
					{
						if (displayed[k].isSeparator) continue;
						displayed[k].selected = newSelected;
					}
				}
				else
				{
					slot.selected = newSelected;
				}
				shiftAnchorDisplayIndex = displayIndex;
			}

			EditorGUILayout.LabelField(slot.name, GUILayout.MinWidth(80));

			GUI.enabled = !inPreview;
			EditorGUI.BeginChangeCheck();
			float newVal = EditorGUILayout.Slider(slot.current, 0f, 100f);
			if (EditorGUI.EndChangeCheck())
			{
				WriteWeights(
					new List<KeyValuePair<BlendshapeSlot, float>> { new KeyValuePair<BlendshapeSlot, float>(slot, newVal) },
					"YBE: Edit Blendshape");
			}
			GUI.enabled = true;

			EditorGUILayout.EndHorizontal();
		}

		// --- Filtering ---

		private List<BlendshapeSlot> GetDisplayedSlots()
		{
			var result = new List<BlendshapeSlot>(slots.Count);
			string selectedGroup = (currentGroupIndex > 0 && currentGroupIndex < groupNames.Count) ? groupNames[currentGroupIndex] : null;
			string filter = string.IsNullOrEmpty(searchFilter) ? null : searchFilter.ToLowerInvariant();

			// Separators act as visible headers only when we're in View All with no active search.
			bool showSeparators = (selectedGroup == null) && (filter == null);

			int lastHeaderIndex = -1;
			bool headerHasChildren = false;

			foreach (var s in slots)
			{
				if (s.isSeparator)
				{
					if (!showSeparators) continue;
					if (lastHeaderIndex >= 0 && !headerHasChildren)
						result.RemoveAt(lastHeaderIndex);
					lastHeaderIndex = result.Count;
					headerHasChildren = false;
					result.Add(s);
					continue;
				}

				if (selectedGroup != null && s.groupName != selectedGroup) continue;
				if (filter != null && s.name.ToLowerInvariant().IndexOf(filter, System.StringComparison.Ordinal) < 0) continue;
				result.Add(s);
				if (lastHeaderIndex >= 0) headerHasChildren = true;
			}

			if (lastHeaderIndex >= 0 && !headerHasChildren)
				result.RemoveAt(lastHeaderIndex);

			return result;
		}

		private static int CountSlotsInList(List<BlendshapeSlot> list)
		{
			int c = 0;
			for (int i = 0; i < list.Count; i++) if (!list[i].isSeparator) c++;
			return c;
		}

		private int CountNonSeparators()
		{
			int c = 0;
			foreach (var s in slots) if (!s.isSeparator) c++;
			return c;
		}

		private int CountSelected()
		{
			int c = 0;
			foreach (var s in slots) if (!s.isSeparator && s.selected) c++;
			return c;
		}
	}
}
