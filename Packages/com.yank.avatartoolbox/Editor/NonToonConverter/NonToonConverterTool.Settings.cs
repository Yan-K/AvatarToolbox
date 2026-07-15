using UnityEditor;

namespace YanK
{
	public partial class NonToonConverterTool
	{
		// ----- output mode -----

		private enum OutputMode { Original = 0, Custom = 1 }

		// ----- options struct -----

		private struct ConverterOptions
		{
			// Fur (default ON)
			public bool ConvertFur;

			// Pipeline (all default OFF)
			public bool      CopyRenderQueue;
			public bool      CopyStencil;
			public bool      CopyCull;
			public bool      CopyZWrite;
			public bool      CopyBlend;
			public ForceMode ForceRenderingMode;

			// Bake (all default ON)
			public bool BakeMainTex;
			public bool BakeAlphaMask;
			public bool BakeShadow;
			public bool BakeEmission;
			public bool BakeNormalMap;
			public bool BakeMasks;
		}

		private enum ForceMode
		{
			Auto        = 0,
			Opaque      = 1,
			Cutout      = 2,
			Transparent = 3
		}

		// ----- state -----

		private ConverterOptions options;
		private bool             advancedFoldout;
		private OutputMode       outputMode;
		private string           customOutputFolder = "";

		// ----- EditorPrefs keys -----

		private const string NC_ConvertFur         = "NC_ConvertFur";
		private const string NC_CopyRenderQueue    = "NC_CopyRenderQueue";
		private const string NC_CopyStencil        = "NC_CopyStencil";
		private const string NC_CopyCull           = "NC_CopyCull";
		private const string NC_CopyZWrite         = "NC_CopyZWrite";
		private const string NC_CopyBlend          = "NC_CopyBlend";
		private const string NC_ForceRenderingMode = "NC_ForceRenderingMode";
		private const string NC_AdvancedFoldout    = "NC_AdvancedFoldout";
		private const string NC_OutputMode         = "NC_OutputMode";
		private const string NC_OutputFolder       = "NC_OutputFolder";
		private const string NC_BakeMainTex        = "NC_BakeMainTex";
		private const string NC_BakeAlphaMask      = "NC_BakeAlphaMask";
		private const string NC_BakeShadow         = "NC_BakeShadow";
		private const string NC_BakeEmission       = "NC_BakeEmission";
		private const string NC_BakeNormalMap      = "NC_BakeNormalMap";
		private const string NC_BakeMasks          = "NC_BakeMasks";

		// ----- load / save -----

		private void LoadOptions()
		{
			options.ConvertFur         = EditorPrefs.GetBool(NC_ConvertFur,         true);
			options.CopyRenderQueue    = EditorPrefs.GetBool(NC_CopyRenderQueue,    false);
			options.CopyStencil        = EditorPrefs.GetBool(NC_CopyStencil,        false);
			options.CopyCull           = EditorPrefs.GetBool(NC_CopyCull,           false);
			options.CopyZWrite         = EditorPrefs.GetBool(NC_CopyZWrite,         false);
			options.CopyBlend          = EditorPrefs.GetBool(NC_CopyBlend,          false);
			options.ForceRenderingMode = (ForceMode)EditorPrefs.GetInt(NC_ForceRenderingMode, 0);
			options.BakeMainTex        = EditorPrefs.GetBool(NC_BakeMainTex,        true);
			options.BakeAlphaMask      = EditorPrefs.GetBool(NC_BakeAlphaMask,      true);
			options.BakeShadow         = EditorPrefs.GetBool(NC_BakeShadow,         false);
			options.BakeEmission       = EditorPrefs.GetBool(NC_BakeEmission,       true);
			options.BakeNormalMap      = EditorPrefs.GetBool(NC_BakeNormalMap,      true);
			options.BakeMasks          = EditorPrefs.GetBool(NC_BakeMasks,          true);
			// Default expanded
			advancedFoldout            = EditorPrefs.GetBool(NC_AdvancedFoldout,    true);
			outputMode                 = (OutputMode)EditorPrefs.GetInt(NC_OutputMode, 0);
			customOutputFolder         = EditorPrefs.GetString(NC_OutputFolder, "");
		}

		private void SetConvertFur(bool v)         { if (options.ConvertFur == v) return; options.ConvertFur = v; EditorPrefs.SetBool(NC_ConvertFur, v); }
		private void SetCopyRenderQueue(bool v)    { if (options.CopyRenderQueue == v) return; options.CopyRenderQueue = v; EditorPrefs.SetBool(NC_CopyRenderQueue, v); }
		private void SetCopyStencil(bool v)        { if (options.CopyStencil == v) return; options.CopyStencil = v; EditorPrefs.SetBool(NC_CopyStencil, v); }
		private void SetCopyCull(bool v)           { if (options.CopyCull == v) return; options.CopyCull = v; EditorPrefs.SetBool(NC_CopyCull, v); }
		private void SetCopyZWrite(bool v)         { if (options.CopyZWrite == v) return; options.CopyZWrite = v; EditorPrefs.SetBool(NC_CopyZWrite, v); }
		private void SetCopyBlend(bool v)          { if (options.CopyBlend == v) return; options.CopyBlend = v; EditorPrefs.SetBool(NC_CopyBlend, v); }
		private void SetForceRenderingMode(ForceMode v) { if (options.ForceRenderingMode == v) return; options.ForceRenderingMode = v; EditorPrefs.SetInt(NC_ForceRenderingMode, (int)v); }
		private void SetAdvancedFoldout(bool v)    { if (advancedFoldout == v) return; advancedFoldout = v; EditorPrefs.SetBool(NC_AdvancedFoldout, v); }
		private void SetOutputMode(OutputMode v)   { if (outputMode == v) return; outputMode = v; EditorPrefs.SetInt(NC_OutputMode, (int)v); }
		private void SetCustomOutputFolder(string v) { if (customOutputFolder == v) return; customOutputFolder = v; EditorPrefs.SetString(NC_OutputFolder, v); }
		private void SetBakeMainTex(bool v)        { if (options.BakeMainTex == v) return; options.BakeMainTex = v; EditorPrefs.SetBool(NC_BakeMainTex, v); }
		private void SetBakeAlphaMask(bool v)      { if (options.BakeAlphaMask == v) return; options.BakeAlphaMask = v; EditorPrefs.SetBool(NC_BakeAlphaMask, v); }
		private void SetBakeShadow(bool v)         { if (options.BakeShadow == v) return; options.BakeShadow = v; EditorPrefs.SetBool(NC_BakeShadow, v); }
		private void SetBakeEmission(bool v)       { if (options.BakeEmission == v) return; options.BakeEmission = v; EditorPrefs.SetBool(NC_BakeEmission, v); }
		private void SetBakeNormalMap(bool v)      { if (options.BakeNormalMap == v) return; options.BakeNormalMap = v; EditorPrefs.SetBool(NC_BakeNormalMap, v); }
		private void SetBakeMasks(bool v)          { if (options.BakeMasks == v) return; options.BakeMasks = v; EditorPrefs.SetBool(NC_BakeMasks, v); }
	}
}
