using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace YanK
{
	/// <summary>
	/// Yan-K NonToon Converter — converts lilToon materials to lilxyzw NonToon / NonToonFur.
	/// </summary>
	public partial class NonToonConverterTool : YanKEditorWindow
	{
		protected override string ToolTitleKey    => "ncTitle";
		protected override string ToolTitleDefault => "Yan-K NonToon Converter";

		[MenuItem("Tools/Yan-K/NonToon Converter")]
		public static void ShowWindow() =>
			GetWindow<NonToonConverterTool>("Yan-K NonToon Converter");

		// ----- persistent simple fields -----

		private readonly List<UnityEngine.Object> roots = new List<UnityEngine.Object>();
		private Vector2 scrollPosition;
		private Vector2 rootsScroll;
		private bool   selectAll;
		private string searchFilter = "";

		// ----- lifecycle -----

		protected override void OnEnable()
		{
			base.OnEnable();
			LoadOptions();
		}

		// ----- main GUI entry -----

		private void OnGUI()
		{
			InitStyles();
			DrawHeader();
			DrawControlPanel();
			DrawStyledSeparator();

			if (conversionSlots.Count > 0)
				DrawMaterialList();
			else
				DrawEmptyState();
		}
	}
}
