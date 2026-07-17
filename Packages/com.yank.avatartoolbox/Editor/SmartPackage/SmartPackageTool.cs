using UnityEngine;
using UnityEditor;

namespace YanK
{
	public partial class SmartPackageTool : YanKEditorWindow
	{
		protected override string ToolTitleKey => "yspTitle";
		protected override string ToolTitleDefault => "Yan-K Smart Package";

		private enum SmartPackageTab { Exporter, Importer }

		private SmartPackageTab currentTab;

		private static class Prefs
		{
			public const string ExcludeExtensions = "YSP_ExcludeExtensions";
			public const string ExcludeGlobs = "YSP_ExcludeGlobs";
			public const string LastExportFolder = "YSP_LastExportFolder";
			public const string LastImportFolder = "YSP_LastImportFolder";
			public const string SortMode = "YSP_SortMode";
			public const string SortAscending = "YSP_SortAscending";
			public const string ConflictPolicy = "YSP_ConflictPolicy";

			public const string DefaultExcludeExtensions = ".cs,.shader";
			public const string DefaultExcludeGlobs = "";
		}

		[MenuItem("Tools/Yan-K/Smart Package")]
		public static void ShowWindow()
		{
			GetWindow<SmartPackageTool>("Yan-K Smart Package");
		}

		private void OnGUI()
		{
			InitStyles();
			DrawHeader();

			string[] tabLabels = {
				L("yspTabExporter", "Exporter"),
				L("yspTabImporter", "Importer")
			};
			int newTab = GUILayout.Toolbar((int)currentTab, tabLabels, GUILayout.Height(22));
			if (newTab != (int)currentTab)
			{
				currentTab = (SmartPackageTab)newTab;
				if (currentTab == SmartPackageTab.Importer && loadedPackages.Count > 0)
					QueueDirtyTargetRefresh();
			}

			GUILayout.Space(4);

			if (currentTab == SmartPackageTab.Exporter)
				DrawExporterTab();
			else
				DrawImporterTab();
		}
	}
}
