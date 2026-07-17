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
			if (string.IsNullOrWhiteSpace(outPath))
				throw new ArgumentException("An output path is required.", nameof(outPath));
			if (entries == null)
				throw new ArgumentNullException(nameof(entries));

			var list = new List<UnityPackageEntry>(entries);
			int total = list.Count;
			string finalPath = Path.GetFullPath(outPath);
			string directory = Path.GetDirectoryName(finalPath);
			if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
				throw new DirectoryNotFoundException("The export directory does not exist: " + directory);

			string tempPath = Path.Combine(
				directory,
				".ysp-" + Guid.NewGuid().ToString("N") + ".tmp");
			bool ownsTempFile = false;

			try
			{
				using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				{
					ownsTempFile = true;
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

				if (File.Exists(finalPath))
					File.Replace(tempPath, finalPath, null);
				else
					File.Move(tempPath, finalPath);
				ownsTempFile = false;
			}
			finally
			{
				if (ownsTempFile && File.Exists(tempPath))
				{
					try
					{
						File.Delete(tempPath);
					}
					catch
					{
						// Do not hide the write/commit error with a cleanup error.
					}
				}
			}
		}
	}
}
