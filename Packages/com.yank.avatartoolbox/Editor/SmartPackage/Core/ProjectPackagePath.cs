using System;
using System.Collections.Generic;
using System.IO;

namespace YanK
{
	/// <summary>
	/// Validates pathnames stored in a .unitypackage before they are used as filesystem paths.
	/// SmartPackage intentionally supports project files outside Assets, but never paths outside
	/// the project itself.
	/// </summary>
	public static class ProjectPackagePath
	{
		private static readonly HashSet<string> ReservedWindowsNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"CON", "PRN", "AUX", "NUL",
			"COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
			"LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
		};

		private static readonly char[] ExplicitInvalidSegmentChars = { '<', '>', ':', '"', '|', '?', '*' };

		public static bool TryNormalize(string rawPath, out string normalizedPath, out string error)
		{
			normalizedPath = null;
			error = null;

			if (string.IsNullOrWhiteSpace(rawPath))
			{
				error = "The package pathname is empty.";
				return false;
			}

			string path = rawPath.Replace('\\', '/');
			if (path.StartsWith("/", StringComparison.Ordinal) || Path.IsPathRooted(path))
			{
				error = "Rooted package paths are not allowed: " + rawPath;
				return false;
			}

			string[] parts = path.Split('/');
			for (int i = 0; i < parts.Length; i++)
			{
				string segment = parts[i];
				if (string.IsNullOrEmpty(segment))
				{
					error = "Empty path segments are not allowed: " + rawPath;
					return false;
				}
				if (segment == "." || segment == "..")
				{
					error = "Relative path segments are not allowed: " + rawPath;
					return false;
				}
				if (segment.EndsWith(" ", StringComparison.Ordinal) || segment.EndsWith(".", StringComparison.Ordinal))
				{
					error = "Path segments may not end with a space or period: " + rawPath;
					return false;
				}
				if (segment.IndexOfAny(ExplicitInvalidSegmentChars) >= 0)
				{
					error = "The package pathname contains invalid filename characters: " + rawPath;
					return false;
				}
				for (int c = 0; c < segment.Length; c++)
				{
					if (char.IsControl(segment[c]))
					{
						error = "The package pathname contains control characters.";
						return false;
					}
				}

				string stem = segment;
				int dot = stem.IndexOf('.');
				if (dot >= 0)
					stem = stem.Substring(0, dot);
				if (ReservedWindowsNames.Contains(stem))
				{
					error = "The package pathname uses a reserved filename: " + rawPath;
					return false;
				}
			}

			if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
			{
				error = "A package pathname may not directly target a .meta file: " + rawPath;
				return false;
			}

			normalizedPath = string.Join("/", parts);
			return true;
		}

		public static bool TryGetAbsolutePath(string projectRoot, string packagePath, out string absolutePath, out string error)
		{
			absolutePath = null;
			if (!TryNormalize(packagePath, out string normalized, out error))
				return false;
			if (string.IsNullOrEmpty(projectRoot))
			{
				error = "The Unity project root is unavailable.";
				return false;
			}

			try
			{
				string canonicalProject = Path.GetFullPath(projectRoot)
					.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				string relative = normalized.Replace('/', Path.DirectorySeparatorChar);
				string candidate = Path.GetFullPath(Path.Combine(canonicalProject, relative));
				string requiredPrefix = canonicalProject + Path.DirectorySeparatorChar;

				if (!candidate.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
				{
					error = "The package pathname resolves outside the Unity project: " + packagePath;
					return false;
				}

				// FullPath containment is lexical. A junction or symbolic link anywhere below
				// the project could otherwise redirect a read/write outside it while the text
				// path still appears contained.
				string current = canonicalProject;
				string[] segments = normalized.Split('/');
				for (int i = 0; i < segments.Length; i++)
				{
					current = Path.Combine(current, segments[i]);
					if (!TryRejectReparsePoint(current, packagePath, out error))
						return false;
				}
				if (!TryRejectReparsePoint(candidate + ".meta", packagePath + ".meta", out error))
					return false;

				absolutePath = candidate;
				return true;
			}
			catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
			{
				error = "Invalid package pathname '" + packagePath + "': " + ex.Message;
				return false;
			}
		}

		private static bool TryRejectReparsePoint(string absolutePath, string displayPath, out string error)
		{
			error = null;
			try
			{
				FileAttributes attributes = File.GetAttributes(absolutePath);
				if ((attributes & FileAttributes.ReparsePoint) != 0)
				{
					error = "Symbolic links and junctions are not valid package targets: " + displayPath;
					return false;
				}
				return true;
			}
			catch (FileNotFoundException)
			{
				return true;
			}
			catch (DirectoryNotFoundException)
			{
				return true;
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				error = "Could not validate package target '" + displayPath + "': " + ex.Message;
				return false;
			}
		}

		public static bool IsPathManaged(string normalizedPath, bool hasValidMetaGuid)
		{
			// AssetDatabase GUID identity only applies to Assets and embedded package assets.
			// Everything else in the Unity project is imported by pathname, even if a custom
			// packer emitted an asset.meta-shaped placeholder for it.
			if (normalizedPath.Equals("Assets", StringComparison.Ordinal)
				|| normalizedPath.StartsWith("Assets/", StringComparison.Ordinal))
				return false;
			if (normalizedPath.Equals("Packages", StringComparison.Ordinal)
				|| normalizedPath.StartsWith("Packages/", StringComparison.Ordinal))
				return !hasValidMetaGuid;
			return true;
		}
	}
}
