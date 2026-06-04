using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace YanK
{
	public static class UnityPackageReader
	{
		public static List<UnityPackageEntry> Read(string filePath)
		{
			var map = new Dictionary<string, UnityPackageEntry>();
			try
			{
				using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
				using (var gz = new GZipStream(fs, CompressionMode.Decompress))
				using (var buffered = new BufferedStream(gz, 64 * 1024))
				{
					var reader = new TarReader(buffered);
					while (reader.MoveNext(out var entry))
					{
						string name = entry.Name ?? string.Empty;
						int slash = name.IndexOf('/');
						if (slash <= 0)
						{
							reader.SkipEntry();
							continue;
						}
						string guid = name.Substring(0, slash);
						string kind = name.Substring(slash + 1);
						if (!GuidUtility.IsValidGuid(guid))
						{
							reader.SkipEntry();
							continue;
						}

						byte[] bytes = reader.ReadEntryBytes();
						if (!map.TryGetValue(guid, out var record))
						{
							record = new UnityPackageEntry { Guid = guid };
							map[guid] = record;
						}

						switch (kind)
						{
							case "asset":
								record.AssetBytes = bytes;
								break;
							case "asset.meta":
								record.MetaBytes = bytes;
								break;
							case "pathname":
								record.AssetPath = GuidUtility.NormalizePathname(bytes);
								break;
							case "preview.png":
								record.PreviewBytes = bytes;
								break;
						}
					}
				}
			}
			catch (InvalidDataException)
			{
				throw new InvalidDataException("Not a valid .unitypackage: " + filePath);
			}
			catch (EndOfStreamException)
			{
				throw new InvalidDataException("Not a valid .unitypackage: " + filePath);
			}

			var result = new List<UnityPackageEntry>(map.Count);
			foreach (var kv in map)
			{
				if (!string.IsNullOrEmpty(kv.Value.AssetPath))
					result.Add(kv.Value);
			}
			return result;
		}

		// Metadata-only scan: reads GUID, pathname and uncompressed asset size,
		// but skips all heavy payload bytes (asset / asset.meta / preview.png).
		// Safe to run off the main thread; touches no Unity API.
		public static List<UnityPackageEntry> ReadMetadata(string filePath)
		{
			var map = new Dictionary<string, UnityPackageEntry>();
			try
			{
				using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
				using (var gz = new GZipStream(fs, CompressionMode.Decompress))
				using (var buffered = new BufferedStream(gz, 64 * 1024))
				{
					var reader = new TarReader(buffered);
					while (reader.MoveNext(out var entry))
					{
						string name = entry.Name ?? string.Empty;
						int slash = name.IndexOf('/');
						if (slash <= 0)
						{
							reader.SkipEntry();
							continue;
						}
						string guid = name.Substring(0, slash);
						string kind = name.Substring(slash + 1);
						if (!GuidUtility.IsValidGuid(guid))
						{
							reader.SkipEntry();
							continue;
						}

						if (!map.TryGetValue(guid, out var record))
						{
							record = new UnityPackageEntry { Guid = guid };
							map[guid] = record;
						}

						if (kind == "pathname")
						{
							byte[] bytes = reader.ReadEntryBytes();
							record.AssetPath = GuidUtility.NormalizePathname(bytes);
						}
						else
						{
							if (kind == "asset")
								record.Size = entry.Size;
							reader.SkipEntry();
						}
					}
				}
			}
			catch (InvalidDataException)
			{
				throw new InvalidDataException("Not a valid .unitypackage: " + filePath);
			}
			catch (EndOfStreamException)
			{
				throw new InvalidDataException("Not a valid .unitypackage: " + filePath);
			}

			var result = new List<UnityPackageEntry>(map.Count);
			foreach (var kv in map)
			{
				if (!string.IsNullOrEmpty(kv.Value.AssetPath))
					result.Add(kv.Value);
			}
			return result;
		}

		// Re-reads the package and loads only the asset / asset.meta payloads for
		// the requested GUIDs, skipping everything else to keep memory low.
		public static Dictionary<string, LoadedAssetBytes> ReadBytesFor(string filePath, ICollection<string> guids)
		{
			var result = new Dictionary<string, LoadedAssetBytes>();
			if (guids == null || guids.Count == 0)
				return result;

			var wanted = guids as HashSet<string> ?? new HashSet<string>(guids);

			try
			{
				using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
				using (var gz = new GZipStream(fs, CompressionMode.Decompress))
				using (var buffered = new BufferedStream(gz, 64 * 1024))
				{
					var reader = new TarReader(buffered);
					while (reader.MoveNext(out var entry))
					{
						string name = entry.Name ?? string.Empty;
						int slash = name.IndexOf('/');
						if (slash <= 0)
						{
							reader.SkipEntry();
							continue;
						}
						string guid = name.Substring(0, slash);
						string kind = name.Substring(slash + 1);
						if (!wanted.Contains(guid) || (kind != "asset" && kind != "asset.meta"))
						{
							reader.SkipEntry();
							continue;
						}

						byte[] bytes = reader.ReadEntryBytes();
						if (!result.TryGetValue(guid, out var record))
						{
							record = new LoadedAssetBytes();
							result[guid] = record;
						}
						if (kind == "asset")
							record.AssetBytes = bytes;
						else
							record.MetaBytes = bytes;
					}
				}
			}
			catch (InvalidDataException)
			{
				throw new InvalidDataException("Not a valid .unitypackage: " + filePath);
			}
			catch (EndOfStreamException)
			{
				throw new InvalidDataException("Not a valid .unitypackage: " + filePath);
			}

			return result;
		}
	}

	public sealed class LoadedAssetBytes
	{
		public byte[] AssetBytes;
		public byte[] MetaBytes;
	}
}
