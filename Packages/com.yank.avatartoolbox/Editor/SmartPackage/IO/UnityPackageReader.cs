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

		// Metadata scan: reads pathnames and the comparatively small .meta members and
		// fingerprints asset payloads. Reading meta is required to verify that the GUID used
		// for preflight is the GUID that will actually be written. With a payload directory,
		// the same pass also writes every asset and meta member there, so Import only moves files.
		// Progress reports compressed bytes read.
		public static List<UnityPackageEntry> ReadMetadata(string filePath, Action<long> progress = null,
			Func<bool> cancelled = null, string payloadDirectory = null)
		{
			if (payloadDirectory != null)
				Directory.CreateDirectory(payloadDirectory);
			var map = new Dictionary<string, UnityPackageEntry>(StringComparer.OrdinalIgnoreCase);
			var ordered = new List<UnityPackageEntry>();
			try
			{
				using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
				using (var observed = new ProgressReadStream(fs, progress, cancelled))
				using (var gz = new GZipStream(observed, CompressionMode.Decompress))
				using (var buffered = new BufferedStream(gz, 64 * 1024))
				{
					var reader = new TarReader(buffered, cancelled);
					while (reader.MoveNext(out var entry))
					{
						if (!TrySplitMemberName(entry.Name, out string guid, out string kind))
						{
							reader.SkipEntry();
							continue;
						}

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
							if (payloadDirectory != null)
								using (var output = new FileStream(Path.Combine(payloadDirectory, guid + ".meta.payload"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
									output.Write(meta, 0, meta.Length);
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
							if (payloadDirectory == null)
								record.AssetHash = reader.ReadEntrySha256();
							else
								using (var output = new FileStream(Path.Combine(payloadDirectory, guid + ".asset.payload"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
									record.AssetHash = reader.CopyEntryTo(output);
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
			// Mono's native inflate reports damaged or non-gzip bytes as a plain IOException.
			catch (IOException ex) when (ex.Message.StartsWith("Corrupted data", StringComparison.Ordinal))
			{
				throw new InvalidDataException("Not a valid .unitypackage: " + filePath + "\nCorrupted data.", ex);
			}

			var result = new List<UnityPackageEntry>(ordered.Count);
			foreach (UnityPackageEntry record in ordered)
			{
				ValidateMetadataRecord(record, filePath);
				result.Add(record);
			}
			return result;
		}

		// Re-reads the package and streams only the selected asset / asset.meta members into
		// staging files, so no payload is held in memory. Import uses this when the payloads
		// written during loading are gone.
		public static Dictionary<string, StagedAssetPayload> StageSelected(
			string filePath, ICollection<string> guids, string stagingDirectory)
		{
			var result = new Dictionary<string, StagedAssetPayload>(StringComparer.OrdinalIgnoreCase);
			if (guids == null || guids.Count == 0)
				return result;
			Directory.CreateDirectory(stagingDirectory);
			var wanted = new HashSet<string>(guids, StringComparer.OrdinalIgnoreCase);

			try
			{
				using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
				using (var gz = new GZipStream(fs, CompressionMode.Decompress))
				using (var buffered = new BufferedStream(gz, 64 * 1024))
				{
					var reader = new TarReader(buffered);
					while (reader.MoveNext(out var entry))
					{
						if (!TrySplitMemberName(entry.Name, out string guid, out string kind)
							|| !wanted.Contains(guid) || (kind != "asset" && kind != "asset.meta"))
						{
							reader.SkipEntry();
							continue;
						}
						if (!result.TryGetValue(guid, out StagedAssetPayload payload))
						{
							payload = new StagedAssetPayload();
							result[guid] = payload;
						}
						bool isMeta = kind == "asset.meta";
						if ((isMeta ? payload.MetaPath : payload.AssetPath) != null)
							throw new InvalidDataException("Duplicate " + kind + " member for GUID " + guid);
						string path = Path.Combine(stagingDirectory, guid + (isMeta ? ".meta.payload" : ".asset.payload"));
						string hash;
						using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
							hash = reader.CopyEntryTo(output, isMeta ? MaxMetaBytes : long.MaxValue);
						if (isMeta)
						{
							payload.MetaPath = path;
							payload.MetaHash = hash;
						}
						else
						{
							payload.AssetPath = path;
							payload.AssetHash = hash;
							payload.AssetSize = entry.Size;
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
			catch (IOException ex) when (ex.Message.StartsWith("Corrupted data", StringComparison.Ordinal))
			{
				throw new InvalidDataException("Not a valid .unitypackage: " + filePath + "\nCorrupted data.", ex);
			}

			return result;
		}

		// Splits "<guid>/<kind>". Archives made by running tar over a package directory
		// prefix every member with "./".
		private static bool TrySplitMemberName(string name, out string guid, out string kind)
		{
			guid = null;
			kind = null;
			if (name == null)
				return false;
			while (name.StartsWith("./", StringComparison.Ordinal))
				name = name.Substring(2);

			int slash = name.IndexOf('/');
			if (slash <= 0)
				return false;
			guid = name.Substring(0, slash).ToLowerInvariant();
			kind = name.Substring(slash + 1);
			return true;
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
				// Unity exports an embedded package's root with only a pathname, and a root's
				// .meta may omit folderAsset. Like Unity, such an entry without an asset is a folder.
				if (!record.HasAssetMember && (!record.HasMetaMember || ProjectPackagePath.IsPackageRoot(normalized)))
					record.IsFolder = true;
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

			// A missing asset member is what marks a folder. IDE-generated metas can omit
			// `folderAsset: yes`, and Unity exports them as they are.
			if (!record.HasAssetMember)
				record.IsFolder = true;
		}

		// Reports compressed bytes read and stops the scan when cancelled. It does not own the source.
		private sealed class ProgressReadStream : Stream
		{
			private readonly Stream source;
			private readonly Action<long> progress;
			private readonly Func<bool> cancelled;
			private long bytesRead;

			public ProgressReadStream(Stream source, Action<long> progress, Func<bool> cancelled)
			{
				this.source = source;
				this.progress = progress;
				this.cancelled = cancelled;
			}

			public override int Read(byte[] buffer, int offset, int count)
			{
				if (cancelled != null && cancelled()) throw new OperationCanceledException();
				int read = source.Read(buffer, offset, count);
				bytesRead += read;
				progress?.Invoke(bytesRead);
				return read;
			}

			public override bool CanRead => true;
			public override bool CanSeek => false;
			public override bool CanWrite => false;
			public override long Length => throw new NotSupportedException();
			public override long Position { get => bytesRead; set => throw new NotSupportedException(); }
			public override void Flush() { }
			public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
			public override void SetLength(long value) => throw new NotSupportedException();
			public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		}
	}

	// Staged asset and meta files for one package GUID, with the SHA-256 they were written with.
	public sealed class StagedAssetPayload
	{
		public string AssetPath;
		public string MetaPath;
		public string AssetHash;
		public string MetaHash;
		public long AssetSize;
	}
}
