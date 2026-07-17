using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;

namespace YanK
{
	public sealed class TarReader
	{
		readonly Stream stream;
		readonly byte[] header = new byte[512];
		long currentEntrySize;
		bool hasCurrentEntry;

		public TarReader(Stream stream)
		{
			this.stream = stream;
		}

		public bool MoveNext(out TarEntry entry)
		{
			entry = null;
			int read = 0;
			while (read < 512)
			{
				int n = stream.Read(header, read, 512 - read);
				if (n <= 0)
				{
					if (read == 0) return false;
					throw new EndOfStreamException("Truncated TAR header.");
				}
				read += n;
			}

			if (IsAllZero(header))
			{
				hasCurrentEntry = false;
				return false;
			}

			string name = ReadString(header, 0, 100);
			string sizeOctal = ReadString(header, 124, 12);
			byte typeFlag = header[156];
			string prefix = ReadString(header, 345, 155);
			string mtimeOctal = ReadString(header, 136, 12);

			long size = ParseOctal(sizeOctal);
			long mtime = string.IsNullOrEmpty(mtimeOctal) ? 0 : ParseOctal(mtimeOctal);

			string fullName = prefix.Length > 0 ? prefix + "/" + name : name;

			entry = new TarEntry
			{
				Name = fullName,
				Size = size,
				TypeFlag = typeFlag,
				ModifiedTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(mtime)
			};

			currentEntrySize = size;
			hasCurrentEntry = true;
			return true;
		}

		public byte[] ReadEntryBytes(long maximumSize = int.MaxValue)
		{
			if (!hasCurrentEntry) throw new InvalidOperationException("No current entry");
			if (currentEntrySize < 0 || currentEntrySize > maximumSize || currentEntrySize > int.MaxValue)
				throw new InvalidDataException("TAR member is too large to buffer: " + currentEntrySize + " bytes.");
			byte[] buffer = new byte[(int)currentEntrySize];
			int read = 0;
			while (read < buffer.Length)
			{
				int n = stream.Read(buffer, read, buffer.Length - read);
				if (n <= 0) throw new EndOfStreamException();
				read += n;
			}
			SkipPadding(currentEntrySize);
			hasCurrentEntry = false;
			currentEntrySize = 0;
			return buffer;
		}

		public string ReadEntrySha256()
		{
			if (!hasCurrentEntry) throw new InvalidOperationException("No current entry");
			using (SHA256 sha = SHA256.Create())
			{
				byte[] buffer = new byte[64 * 1024];
				long remaining = currentEntrySize;
				while (remaining > 0)
				{
					int requested = (int)Math.Min(buffer.Length, remaining);
					int read = stream.Read(buffer, 0, requested);
					if (read <= 0) throw new EndOfStreamException();
					sha.TransformBlock(buffer, 0, read, buffer, 0);
					remaining -= read;
				}
				sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
				SkipPadding(currentEntrySize);
				hasCurrentEntry = false;
				currentEntrySize = 0;
				return Convert.ToBase64String(sha.Hash);
			}
		}

		public bool SkipEntry()
		{
			if (!hasCurrentEntry) return false;
			long total = currentEntrySize + GetPadding(currentEntrySize);
			SkipBytes(total);
			hasCurrentEntry = false;
			currentEntrySize = 0;
			return true;
		}

		void SkipPadding(long size)
		{
			long padding = GetPadding(size);
			if (padding > 0) SkipBytes(padding);
		}

		static long GetPadding(long size)
		{
			long remainder = size % 512;
			return remainder == 0 ? 0 : 512 - remainder;
		}

		void SkipBytes(long count)
		{
			if (stream.CanSeek)
			{
				stream.Seek(count, SeekOrigin.Current);
				return;
			}
			byte[] buf = new byte[Math.Min(4096, count)];
			long remaining = count;
			while (remaining > 0)
			{
				int toRead = (int)Math.Min(buf.Length, remaining);
				int n = stream.Read(buf, 0, toRead);
				if (n <= 0) throw new EndOfStreamException();
				remaining -= n;
			}
		}

		static bool IsAllZero(byte[] block)
		{
			for (int i = 0; i < block.Length; i++)
				if (block[i] != 0) return false;
			return true;
		}

		static string ReadString(byte[] buffer, int offset, int length)
		{
			int end = offset;
			int limit = offset + length;
			while (end < limit && buffer[end] != 0) end++;
			return Encoding.ASCII.GetString(buffer, offset, end - offset);
		}

		static long ParseOctal(string s)
		{
			s = s.Trim().Trim('\0').Trim();
			if (s.Length == 0) return 0;
			long value = 0;
			try
			{
				for (int i = 0; i < s.Length; i++)
				{
					char c = s[i];
					if (c < '0' || c > '7')
						throw new InvalidDataException("Invalid octal value in TAR header.");
					checked { value = (value << 3) + (c - '0'); }
				}
			}
			catch (OverflowException ex)
			{
				throw new InvalidDataException("Octal value overflows a TAR header field.", ex);
			}
			return value;
		}
	}
}
