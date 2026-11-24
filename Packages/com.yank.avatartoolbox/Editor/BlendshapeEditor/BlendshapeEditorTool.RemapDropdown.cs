using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.IMGUI.Controls;

namespace YanK
{
	public partial class BlendshapeEditorTool
	{
		// AdvancedDropdown provides built-in searching + hierarchical navigation, which is
		// necessary because a single avatar can have 600+ blendshapes and a flat Popup is unusable.

		private sealed class BlendshapeSlotDropdownItem : AdvancedDropdownItem
		{
			public int SlotIndex;
			public BlendshapeSlotDropdownItem(string name, int slotIndex) : base(name) { SlotIndex = slotIndex; }
		}

		private sealed class BlendshapeSlotDropdown : AdvancedDropdown
		{
			private readonly List<BlendshapeSlot> allSlots;
			private readonly Action<int> onPicked;
			private readonly string ignoreLabel;
			private readonly string groupGeneralDisplay;

			public BlendshapeSlotDropdown(AdvancedDropdownState state, List<BlendshapeSlot> slots, string ignoreLabel, string generalLabel, Action<int> onPicked)
				: base(state)
			{
				this.allSlots = slots;
				this.onPicked = onPicked;
				this.ignoreLabel = ignoreLabel;
				this.groupGeneralDisplay = generalLabel;
				minimumSize = new Vector2(280f, 420f);
			}

			protected override AdvancedDropdownItem BuildRoot()
			{
				var root = new AdvancedDropdownItem("Blendshape");
				root.AddChild(new BlendshapeSlotDropdownItem(ignoreLabel, -1));
				root.AddSeparator();

				// Preserve insertion order of groups as encountered in the slots list. Each group's
				// items are kept here rather than read back from a parent item: Unity 6.6 turned
				// AdvancedDropdownItem.children into an error, and its replacement is 6.6-only.
				var groupItems = new Dictionary<string, List<AdvancedDropdownItem>>();
				var groupOrder = new List<string>();

				for (int i = 0; i < allSlots.Count; i++)
				{
					var s = allSlots[i];
					if (s.isSeparator) continue;

					string gKey = string.IsNullOrEmpty(s.groupName) ? GroupGeneralKey : s.groupName;
					if (!groupItems.TryGetValue(gKey, out var items))
					{
						items = new List<AdvancedDropdownItem>();
						groupItems[gKey] = items;
						groupOrder.Add(gKey);
					}
					items.Add(new BlendshapeSlotDropdownItem(s.name, i));
				}

				// If the only group is "General", flatten so the user doesn't have to click a submenu.
				if (groupOrder.Count == 1)
				{
					foreach (var item in groupItems[groupOrder[0]]) root.AddChild(item);
				}
				else
				{
					foreach (var g in groupOrder)
					{
						var parent = new AdvancedDropdownItem(g == GroupGeneralKey ? groupGeneralDisplay : g);
						foreach (var item in groupItems[g]) parent.AddChild(item);
						root.AddChild(parent);
					}
				}

				return root;
			}

			protected override void ItemSelected(AdvancedDropdownItem item)
			{
				int idx = item is BlendshapeSlotDropdownItem bi ? bi.SlotIndex : -1;
				onPicked?.Invoke(idx);
			}
		}

		private AdvancedDropdownState cachedRemapDropdownState;

		private void ShowRemapDropdown(Rect rect, Action<int> onPicked)
		{
			if (cachedRemapDropdownState == null) cachedRemapDropdownState = new AdvancedDropdownState();
			var dd = new BlendshapeSlotDropdown(
				cachedRemapDropdownState,
				slots,
				L("bseRemapIgnore", "(Ignore)"),
				L("bseGroupGeneral", "General"),
				onPicked);
			dd.Show(rect);
		}
	}
}
