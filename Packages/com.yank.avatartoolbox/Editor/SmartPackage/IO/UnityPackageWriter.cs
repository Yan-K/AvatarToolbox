using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace YanK
{
	public static class UnityPackageWriter
	{
		public static void Write(string outPath, IEnumerable<UnityPackageEntry> entries, Action<int, int> onProgress = null)
		{
			var list = new List<UnityPackageEntry>(entries);
			int total = list.Count;

			using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
			using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
			{
				var writer = new TarWriter(gz);
				for (int i = 0; i < list.Count; i++)
				{
					var entry = list[i];
					string guid = entry.Guid;

					if (entry.AssetBytes != null)
						writer.WriteEntry(TarEntry.File(guid + "/asset", entry.AssetBytes.Length), entry.AssetBytes);

					byte[] meta = entry.MetaBytes ?? Array.Empty<byte>();
					writer.WriteEntry(TarEntry.File(guid + "/asset.meta", meta.Length), meta);

					byte[] pathBytes = Encoding.UTF8.GetBytes(entry.AssetPath ?? string.Empty);
					writer.WriteEntry(TarEntry.File(guid + "/pathname", pathBytes.Length), pathBytes);

					if (entry.PreviewBytes != null)
						writer.WriteEntry(TarEntry.File(guid + "/preview.png", entry.PreviewBytes.Length), entry.PreviewBytes);

					onProgress?.Invoke(i + 1, total);
				}
				writer.Close();
			}
		}
	}
}
