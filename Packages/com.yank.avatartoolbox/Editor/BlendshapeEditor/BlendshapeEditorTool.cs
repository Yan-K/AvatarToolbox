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

		private const string AutoAssignRootPrefKey = "YBE_AutoAssignRoot";

		private static readonly Color AutoOnColor = new Color(0.3f, 1f, 0.3f);
		private static readonly Color AutoOffColor = new Color(1f, 0.3f, 0.3f);

		// --- Window state (serialized so the target survives script reloads) ---
		[SerializeField] private GameObject avatarRoot;
		[SerializeField] private SkinnedMeshRenderer targetRenderer;
		private bool autoAssignRoot = true;

		private readonly List<BlendshapeSlot> slots = new List<BlendshapeSlot>();
		private Mesh scannedMesh; // Mesh the slots were built from; a mismatch triggers a rescan.
		private readonly List<string> groupNames = new List<string>(); // index 0 reserved for "View All"
		private int currentGroupIndex;

		private string searchFilter = "";
		private Vector2 scrollPosition;

		// Tracks last checkbox click (display-list index) for shift-range selection.
		private int shiftAnchorDisplayIndex = -1;

		// Batch slider
		private float globalBatchValue;

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
			autoAssignRoot = EditorPrefs.GetBool(AutoAssignRootPrefKey, true);
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
			RefreshFromRenderer();
			RefreshUnsavedState();
			Repaint();
		}

		// Picks up weight changes made elsewhere (Inspector, Animation / Timeline preview).
		private void OnInspectorUpdate()
		{
			bool changed = RefreshFromRenderer();
			RefreshUnsavedState();
			if (changed) Repaint();
		}

		private void OnGUI()
		{
			InitStyles();
			if (Event.current.type == EventType.Layout)
			{
				RefreshFromRenderer();
				RefreshUnsavedState();
			}
			DrawHeader();

			if (targetRenderer == null || targetRenderer.sharedMesh == null)
			{
				DrawCenteredMessage(
					autoAssignRoot
						? L("bseNoSMRAuto", "Assign a SkinnedMeshRenderer above to begin.")
						: L("bseNoSMR", "Assign an Avatar Root and a SkinnedMeshRenderer above to begin."),
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

			// Row 2: Avatar Root | Auto | ✕  (field and ✕ are locked while Auto is on)
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField(L("bseRoot", "Avatar Root"), EditorStyles.boldLabel, GUILayout.Width(100));
			GameObject newRoot;
			using (new EditorGUI.DisabledScope(autoAssignRoot))
				newRoot = (GameObject)EditorGUILayout.ObjectField(avatarRoot, typeof(GameObject), true);
			Color prevBackground = GUI.backgroundColor;
			GUI.backgroundColor = autoAssignRoot ? AutoOnColor : AutoOffColor;
			bool toggleAuto = GUILayout.Button(L("bseAutoRoot", "Auto"), GUILayout.Width(44), GUILayout.Height(18));
			GUI.backgroundColor = prevBackground;
			using (new EditorGUI.DisabledScope(autoAssignRoot || avatarRoot == null))
			{
				if (GUILayout.Button("✕", GUILayout.Width(22), GUILayout.Height(18)))
					newRoot = null;
			}
			EditorGUILayout.EndHorizontal();

			// Target changes may open dialogs and change the layout, so end this GUI pass right after them.
			if (toggleAuto)
			{
				SetAutoAssignRoot(!autoAssignRoot);
				GUIUtility.ExitGUI();
			}
			if (newRoot != avatarRoot)
			{
				ChangeAvatarRoot(newRoot);
				GUIUtility.ExitGUI();
			}

			// Row 3: SMR (X button always visible, disabled when empty)
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField(L("bseSMR", "Skinned Mesh Renderer"), EditorStyles.boldLabel, GUILayout.Width(100));
			var newSMR = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(targetRenderer, typeof(SkinnedMeshRenderer), true);
			using (new EditorGUI.DisabledScope(targetRenderer == null))
			{
				if (GUILayout.Button("✕", GUILayout.Width(22), GUILayout.Height(18)))
					newSMR = null;
			}
			EditorGUILayout.EndHorizontal();

			if (newSMR != targetRenderer)
			{
				ChangeTargetRenderer(newSMR);
				GUIUtility.ExitGUI();
			}

			GUILayout.Space(4);
		}

		// --- Target changes ---

		// Manual root change. The renderer is kept only while it still lives under the new root.
		private void ChangeAvatarRoot(GameObject newRoot)
		{
			if (newRoot != null && newRoot.GetComponentInParent<Animator>() == null && newRoot.GetComponent<Animator>() == null)
			{
				if (!EditorUtility.DisplayDialog(
					L("bseNoAnimatorTitle", "No Animator"),
					L("bseNoAnimatorMsg", "The selected GameObject has no Animator. Animation paths may be incorrect. Continue anyway?"),
					"OK", "Cancel"))
					return;
			}

			var keptRenderer = newRoot != null && targetRenderer != null && targetRenderer.transform.IsChildOf(newRoot.transform)
				? targetRenderer
				: null;
			if (keptRenderer != targetRenderer && !ConfirmUnsavedEdits()) return;

			avatarRoot = newRoot;
			SetTargetRenderer(keptRenderer);
		}

		private void ChangeTargetRenderer(SkinnedMeshRenderer newSMR)
		{
			GameObject newRoot = avatarRoot;
			if (autoAssignRoot)
			{
				newRoot = newSMR != null ? FindAvatarRoot(newSMR.transform) : null;
			}
			else if (newSMR != null && avatarRoot != null && !newSMR.transform.IsChildOf(avatarRoot.transform))
			{
				EditorUtility.DisplayDialog(
					L("bseSMRNotInRootTitle", "Invalid SkinnedMeshRenderer"),
					L("bseSMRNotInRoot", "The SkinnedMeshRenderer must be a child of the Avatar Root."),
					"OK");
				return;
			}

			if (!ConfirmUnsavedEdits()) return;

			avatarRoot = newRoot;
			SetTargetRenderer(newSMR);
		}

		private void SetTargetRenderer(SkinnedMeshRenderer smr)
		{
			if (inPreview) CancelPreview();
			// A different target starts a new session (pending edits were resolved by ConfirmUnsavedEdits).
			if (smr != targetRenderer) ClearOriginals();
			targetRenderer = smr;
			shiftAnchorDisplayIndex = -1;
			RescanSlots();
			RefreshUnsavedState();
		}

		private void SetAutoAssignRoot(bool value)
		{
			autoAssignRoot = value;
			EditorPrefs.SetBool(AutoAssignRootPrefKey, value);
			// The renderer stays the same, so edits stay in session; only the root is re-derived.
			if (value) avatarRoot = targetRenderer != null ? FindAvatarRoot(targetRenderer.transform) : null;
		}

		// --- Avatar root detection ---

		// Components that mark the top of an avatar, matched by type name so no SDK reference is needed.
		private static readonly HashSet<string> AvatarRootComponentNames = new HashSet<string>
		{
			"VRC_AvatarDescriptor", "VRCAvatarDescriptor", // VRChat
			"VRMMeta", "Vrm10Instance",                     // VRM 0.x / 1.0
			"CVRAvatar",                                    // ChilloutVR
		};

		/// <summary>
		/// Walks up from <paramref name="start"/> to the GameObject animations for this avatar are rooted at:
		/// the nearest avatar descriptor, else the outermost Animator with a humanoid Avatar (so an outfit's own
		/// Animator nested inside the avatar is skipped), else the nearest Animator with any valid Avatar,
		/// else the nearest Animator. Returns null when no candidate exists.
		/// </summary>
		private static GameObject FindAvatarRoot(Transform start)
		{
			Transform outermostHumanoid = null, nearestWithAvatar = null, nearestAnimator = null;
			for (var t = start; t != null; t = t.parent)
			{
				if (HasAvatarRootComponent(t)) return t.gameObject;

				var animator = t.GetComponent<Animator>();
				if (animator == null) continue;
				if (nearestAnimator == null) nearestAnimator = t;

				var avatar = animator.avatar;
				if (avatar == null || !avatar.isValid) continue;
				if (nearestWithAvatar == null) nearestWithAvatar = t;
				if (avatar.isHuman) outermostHumanoid = t;
			}

			var root = outermostHumanoid != null ? outermostHumanoid
				: nearestWithAvatar != null ? nearestWithAvatar
				: nearestAnimator;
			return root != null ? root.gameObject : null;
		}

		private static bool HasAvatarRootComponent(Transform t)
		{
			foreach (var component in t.GetComponents<MonoBehaviour>())
			{
				if (component == null) continue; // Missing script
				for (var type = component.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
				{
					if (AvatarRootComponentNames.Contains(type.Name)) return true;
				}
			}
			return false;
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
