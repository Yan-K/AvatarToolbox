using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace YanK
{
	public partial class BlendshapeEditorTool : YanKEditorWindow
	{
		protected override string ToolTitleKey => "bseTitle";
		protected override string ToolTitleDefault => "Yan-K Blendshape Editor";

		private enum AnimTab { Import, Export }

		// --- Window state ---
		private GameObject avatarRoot;
		private SkinnedMeshRenderer targetRenderer;

		private readonly List<BlendshapeSlot> slots = new List<BlendshapeSlot>();
		private readonly List<string> groupNames = new List<string>(); // index 0 reserved for "View All"
		private int currentGroupIndex;

		private string searchFilter = "";
		private Vector2 scrollPosition;

		// Tracks last checkbox click (display-list index) for shift-range selection.
		private int shiftAnchorDisplayIndex = -1;

		// Batch slider
		private float globalBatchValue;
		private Dictionary<int, float> batchDragSnapshot; // Captured at drag start for proper RecordObject Undo.

		// Animation tab (top section) state
		private bool animationFoldout = false;
		private bool resetFoldout = false;
		private Vector2 outerScrollPosition;
		private AnimTab animTab = AnimTab.Import;
		private ExportMode exportMode = ExportMode.AllBlendshapes;
		private ImportMode importMode = ImportMode.Overlay;

		// Import preview state
		private AnimationClip importClip;
		private float importNormalizedTime;
		private bool importClipHasMultipleKeyframes;
		private bool inPreview;
		private Dictionary<int, float> previewSnapshot;
		private readonly List<RemapEntry> remapEntries = new List<RemapEntry>();
		private readonly HashSet<string> customImportNames = new HashSet<string>();
		private string customImportFilter = "";
		private Vector2 remapScroll;
		private Vector2 customImportScroll;

		[MenuItem("Tools/Yan-K/Blendshape Editor")]
		public static void ShowWindow()
		{
			GetWindow<BlendshapeEditorTool>("Yan-K Blendshape Editor");
		}

		protected override void OnEnable()
		{
			base.OnEnable();
			Undo.undoRedoPerformed += OnUndoRedoPerformed;
		}

		private void OnDisable()
		{
			Undo.undoRedoPerformed -= OnUndoRedoPerformed;
			// If user closes the window while previewing, restore original values.
			if (inPreview) CancelPreview();
		}

		private void OnUndoRedoPerformed()
		{
			// Re-sync slot.current from actual SMR weights
			if (targetRenderer != null && targetRenderer.sharedMesh != null)
			{
				for (int i = 0; i < slots.Count; i++)
				{
					if (!slots[i].isSeparator && slots[i].index < targetRenderer.sharedMesh.blendShapeCount)
						slots[i].current = targetRenderer.GetBlendShapeWeight(slots[i].index);
				}
			}
			Repaint();
		}

		private void OnGUI()
		{
			InitStyles();
			DrawHeader();

			if (targetRenderer == null || targetRenderer.sharedMesh == null)
			{
				DrawCenteredMessage(
					L("bseNoSMR", "Assign an Avatar Root and a SkinnedMeshRenderer above to begin."),
					"d_SkinnedMeshRenderer Icon");
				return;
			}

			DrawAnimationTab();
			DrawStyledSeparator();
			DrawGlobalControls();
			DrawStyledSeparator();
			DrawGroupTabBar();
			DrawSearchAndStatus();
			DrawSlotList();
		}

		protected override void DrawHeader()
		{
			base.DrawHeader();

			// Row 2: Avatar Root (X button always visible, disabled when empty)
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField(L("bseRoot", "Avatar Root"), EditorStyles.boldLabel, GUILayout.Width(100));
			var newRoot = (GameObject)EditorGUILayout.ObjectField(avatarRoot, typeof(GameObject), true);
			GUI.enabled = avatarRoot != null;
			if (GUILayout.Button("✕", GUILayout.Width(22), GUILayout.Height(18)))
				newRoot = null;
			GUI.enabled = true;
			EditorGUILayout.EndHorizontal();

			if (newRoot != avatarRoot)
			{
				if (newRoot != null && newRoot.GetComponentInParent<Animator>() == null && newRoot.GetComponent<Animator>() == null)
				{
					if (!EditorUtility.DisplayDialog(
						L("bseNoAnimatorTitle", "No Animator"),
						L("bseNoAnimatorMsg", "The selected GameObject has no Animator. Animation paths may be incorrect. Continue anyway?"),
						"OK", "Cancel"))
					{
						newRoot = avatarRoot;
					}
				}
				if (newRoot != avatarRoot)
				{
					if (inPreview) CancelPreview();
					avatarRoot = newRoot;
					if (avatarRoot == null || (targetRenderer != null && !targetRenderer.transform.IsChildOf(avatarRoot.transform)))
						targetRenderer = null;
					RescanSlots();
				}
			}

			// Row 3: SMR (X button always visible, no Rescan button)
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField(L("bseSMR", "Skinned Mesh Renderer"), EditorStyles.boldLabel, GUILayout.Width(100));
			var newSMR = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(targetRenderer, typeof(SkinnedMeshRenderer), true);
			GUI.enabled = targetRenderer != null;
			if (GUILayout.Button("✕", GUILayout.Width(22), GUILayout.Height(18)))
				newSMR = null;
			GUI.enabled = true;
			EditorGUILayout.EndHorizontal();

			if (newSMR != targetRenderer)
			{
				if (newSMR != null && avatarRoot != null && !newSMR.transform.IsChildOf(avatarRoot.transform))
				{
					EditorUtility.DisplayDialog(
						L("bseSMRNotInRootTitle", "Invalid SkinnedMeshRenderer"),
						L("bseSMRNotInRoot", "The SkinnedMeshRenderer must be a child of the Avatar Root."),
						"OK");
				}
				else
				{
					if (inPreview) CancelPreview();
					targetRenderer = newSMR;
					RescanSlots();
				}
			}

			GUILayout.Space(4);
		}

		private void DrawAnimationTab()
		{
			DrawGroupBox(() =>
			{
				EditorGUILayout.BeginHorizontal();
				animationFoldout = EditorGUILayout.Foldout(animationFoldout, L("bseAnimation", "Animation"), true, EditorStyles.foldoutHeader);
				string[] tabs = { L("bseImportTitle", "Import Animation"), L("bseExportTitle", "Export Animation") };
				int cur = (int)animTab;
				int newTab = GUILayout.Toolbar(cur, tabs, GUILayout.Height(20));
				if (newTab != cur)
				{
					if (inPreview) CancelPreview();
					animTab = (AnimTab)newTab;
				}
				EditorGUILayout.EndHorizontal();

				if (!animationFoldout) return;

				GUILayout.Space(4);

				if (animTab == AnimTab.Import) DrawImportContent();
				else DrawExportContent();
			});
		}
	}
}
