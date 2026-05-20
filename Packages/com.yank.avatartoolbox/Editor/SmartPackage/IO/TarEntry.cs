using System;

namespace YanK
{
	public sealed class TarEntry
	{
		public string Name;
		public long Size;
		public byte TypeFlag;
		public DateTime ModifiedTime;

		public static TarEntry File(string name, long size)
		{
			return new TarEntry
			{
				Name = name,
				Size = size,
				TypeFlag = (byte)'0',
				ModifiedTime = DateTime.UtcNow
			};
		}

		public static TarEntry Directory(string name)
		{
			return new TarEntry
			{
				Name = name,
				Size = 0,
				TypeFlag = (byte)'5',
				ModifiedTime = DateTime.UtcNow
			};
		}
	}
}
