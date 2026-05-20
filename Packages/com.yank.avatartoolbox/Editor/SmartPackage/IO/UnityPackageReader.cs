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
	}
}
