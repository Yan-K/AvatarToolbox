namespace YanK
{
	public sealed class UnityPackageEntry
	{
		public string Guid;
		public string AssetPath;
		public byte[] AssetBytes;
		public byte[] MetaBytes;
		public byte[] PreviewBytes;

		public long TotalSize
		{
			get
			{
				long total = 0;
				if (AssetBytes != null) total += AssetBytes.Length;
				if (MetaBytes != null) total += MetaBytes.Length;
				if (PreviewBytes != null) total += PreviewBytes.Length;
				return total;
			}
		}
	}
}
