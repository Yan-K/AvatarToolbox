namespace YanK
{
	public sealed class UnityPackageEntry
	{
		// GUID used for the top-level directory inside the .unitypackage. For normal
		// Unity assets this must match MetaGuid. Path-managed project files may have
		// an archive GUID without having a Unity .meta identity.
		public string Guid;
		public string MetaGuid;
		public string AssetPath;
		public byte[] AssetBytes;
		public byte[] MetaBytes;
		public byte[] PreviewBytes;
		public int EntryOrder;
		public bool HasAssetMember;
		public bool HasMetaMember;
		public bool HasPathnameMember;
		public bool IsFolder;
		public bool IsPathManaged;
		public string ValidationError;
		public string AssetHash;
		public string MetaHash;

		public string IdentityGuid => IsPathManaged ? null : MetaGuid;

		// Uncompressed asset payload size in bytes, captured during a metadata-only
		// scan when AssetBytes is not loaded into memory.
		public long Size;

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
