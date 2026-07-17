using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace YanK
{
	public static class UnityPackageReader
	{
		private const long MaxPathnameBytes = 1024L * 1024L;
		private const long MaxMetaBytes = 64L * 1024L * 1024L;

		public static List<UnityPackageEntry> Read(string filePath)
		{
			List<UnityPackageEntry> result = ReadMetadata(filePath);
			var guids = new List<string>(result.Count);
			for (int i = 0; i < result.Count; i++)
				guids.Add(result[i].Guid);
			Dictionary<string, LoadedAssetBytes> payloads = ReadBytesFor(filePath, guids);
			for (int i = 0; i < result.Count; i++)
			{
				UnityPackageEntry entry = result[i];
				if (!payloads.TryGetValue(entry.Guid, out LoadedAssetBytes payload))
					continue;
				entry.AssetBytes = payload.AssetBytes;
				entry.MetaBytes = entry.HasMetaMember ? payload.MetaBytes : null;
			}
			return result;
		}

		// Metadata scan: reads pathnames and the comparatively small .meta members,
		// but skips asset/preview payloads. Reading meta is required to verify that the
		// GUID used for preflight is the GUID that will actually be written.
		public static List<UnityPackageEntry> ReadMetadata(string filePath)
		{
			var map = new Dictionary<string, UnityPackageEntry>(StringComparer.OrdinalIgnoreCase);
			var ordered = new List<UnityPackageEntry>();
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

						string guid = name.Substring(0, slash).ToLowerInvariant();
						string kind = name.Substring(slash + 1);
						bool knownKind = kind == "asset" || kind == "asset.meta"
							|| kind == "pathname" || kind == "preview.png";
						if (!GuidUtility.IsValidGuid(guid) || !knownKind)
						{
							reader.SkipEntry();
							continue;
						}
						if (!map.TryGetValue(guid, out var record))
						{
							record = new UnityPackageEntry { Guid = guid, EntryOrder = ordered.Count };
							map[guid] = record;
							ordered.Add(record);
						}

						if (kind == "pathname")
						{
							if (record.HasPathnameMember)
								throw new InvalidDataException("Duplicate pathname member for GUID " + guid);
							record.AssetPath = GuidUtility.NormalizePathname(reader.ReadEntryBytes(MaxPathnameBytes));
							record.HasPathnameMember = true;
						}
						else if (kind == "asset.meta")
						{
							if (record.HasMetaMember)
								throw new InvalidDataException("Duplicate asset.meta member for GUID " + guid);
							byte[] meta = reader.ReadEntryBytes(MaxMetaBytes);
							record.HasMetaMember = true;
							record.MetaGuid = GuidUtility.ExtractGuidFromMeta(meta);
							record.IsFolder = GuidUtility.MetaDeclaresFolder(meta);
							record.MetaHash = GuidUtility.ComputeSha256(meta);
						}
						else if (kind == "asset")
						{
							if (record.HasAssetMember)
								throw new InvalidDataException("Duplicate asset member for GUID " + guid);
							record.HasAssetMember = true;
							record.Size = entry.Size;
							record.AssetHash = reader.ReadEntrySha256();
						}
						else
						{
							reader.SkipEntry();
						}
					}
				}
			}
			catch (InvalidDataException ex)
			{
				throw new InvalidDataException("Not a valid .unitypackage: " + filePath + "\n" + ex.Message, ex);
			}
			catch (EndOfStreamException ex)
			{
				throw new InvalidDataException("Not a valid .unitypackage: " + filePath, ex);
			}

			var result = new List<UnityPackageEntry>(ordered.Count);
			foreach (UnityPackageEntry record in ordered)
			{
				ValidateMetadataRecord(record, filePath);
				result.Add(record);
			}
			return result;
		}

		// Re-reads the package and loads only the asset / asset.meta payloads for
		// the requested GUIDs, skipping everything else to keep memory low.
		public static Dictionary<string, LoadedAssetBytes> ReadBytesFor(string filePath, ICollection<string> guids)
		{
			var result = new Dictionary<string, LoadedAssetBytes>(StringComparer.OrdinalIgnoreCase);
			if (guids == null || guids.Count == 0)
				return result;

			var wanted = new HashSet<string>(guids, StringComparer.OrdinalIgnoreCase);

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
						string guid = name.Substring(0, slash).ToLowerInvariant();
						string kind = name.Substring(slash + 1);
						if (!wanted.Contains(guid) || (kind != "asset" && kind != "asset.meta"))
						{
							reader.SkipEntry();
							continue;
						}
						byte[] bytes = reader.ReadEntryBytes(kind == "asset.meta" ? MaxMetaBytes : int.MaxValue);
						if (!result.TryGetValue(guid, out var record))
						{
							record = new LoadedAssetBytes();
							result[guid] = record;
						}
						if (kind == "asset")
						{
							if (record.AssetBytes != null)
								throw new InvalidDataException("Duplicate asset member for GUID " + guid);
							record.AssetBytes = bytes;
						}
						else
						{
							if (record.MetaBytes != null)
								throw new InvalidDataException("Duplicate asset.meta member for GUID " + guid);
							record.MetaBytes = bytes;
						}
					}
				}
			}
			catch (InvalidDataException ex)
			{
				throw new InvalidDataException("Not a valid .unitypackage: " + filePath + "\n" + ex.Message, ex);
			}
			catch (EndOfStreamException ex)
			{
				throw new InvalidDataException("Not a valid .unitypackage: " + filePath, ex);
			}

			return result;
		}

		private static void ValidateMetadataRecord(UnityPackageEntry record, string filePath)
		{
			if (record == null || !record.HasPathnameMember || string.IsNullOrEmpty(record.AssetPath))
				throw new InvalidDataException("Package entry is missing a pathname in " + filePath);

			if (!ProjectPackagePath.TryNormalize(record.AssetPath, out string normalized, out string pathError))
				throw new InvalidDataException(pathError);
			record.AssetPath = normalized;

			bool validMetaGuid = GuidUtility.IsValidGuid(record.MetaGuid);
			record.IsPathManaged = ProjectPackagePath.IsPathManaged(normalized, validMetaGuid);

			if (record.IsPathManaged)
			{
				if (!record.HasAssetMember && !record.IsFolder)
					throw new InvalidDataException("Path-managed project entry has no payload and is not a folder: " + normalized);

				// Some package writers preserve the unitypackage member shape by emitting an
				// asset.meta for project files outside AssetDatabase. These entries have path
				// identity, not Unity GUID identity, so do not write that placeholder beside
				// the destination file.
				record.HasMetaMember = false;
				record.MetaGuid = null;
				record.MetaHash = null;
				return;
			}

			if (record.HasMetaMember)
			{
				if (!validMetaGuid)
					throw new InvalidDataException("Entry has an asset.meta without a valid GUID: " + normalized);
				if (!string.Equals(record.Guid, record.MetaGuid, StringComparison.OrdinalIgnoreCase))
					throw new InvalidDataException("Archive GUID does not match asset.meta GUID for " + normalized);
			}

			if (!record.HasMetaMember)
				throw new InvalidDataException("Unity asset entry is missing asset.meta: " + normalized);
			if (!record.HasAssetMember && !record.IsFolder)
				throw new InvalidDataException("Unity asset entry has no payload and is not a folder: " + normalized);
		}
	}

	public sealed class LoadedAssetBytes
	{
		public byte[] AssetBytes;
		public byte[] MetaBytes;
	}
}
