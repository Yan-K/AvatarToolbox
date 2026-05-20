using System;
using System.IO;
using System.Text;

namespace YanK
{
	public sealed class TarWriter
	{
		readonly Stream stream;
		static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		public TarWriter(Stream stream)
		{
			this.stream = stream;
		}

		public void WriteEntry(TarEntry entry, byte[] content)
		{
			if (entry == null) throw new ArgumentNullException(nameof(entry));
			byte[] data = content ?? Array.Empty<byte>();
			byte[] header = BuildHeader(entry, data.LongLength);
			stream.Write(header, 0, header.Length);
			if (data.Length > 0)
			{
				stream.Write(data, 0, data.Length);
				int remainder = data.Length % 512;
				if (remainder != 0)
				{
					byte[] pad = new byte[512 - remainder];
					stream.Write(pad, 0, pad.Length);
				}
			}
		}

		public void Close()
		{
			byte[] zero = new byte[1024];
			stream.Write(zero, 0, zero.Length);
		}

		static byte[] BuildHeader(TarEntry entry, long size)
		{
			byte[] header = new byte[512];

			string name = entry.Name ?? string.Empty;
			string prefix = string.Empty;

			if (name.Length > 100)
			{
				int splitAt = -1;
				for (int i = Math.Min(name.Length - 1, 155); i > 0; i--)
				{
					if (name[i] == '/' && (name.Length - i - 1) <= 100 && i <= 155)
					{
						splitAt = i;
						break;
					}
				}
				if (splitAt <= 0)
					throw new NotSupportedException("Tar entry name too long: " + name);
				prefix = name.Substring(0, splitAt);
				name = name.Substring(splitAt + 1);
				if (name.Length > 100 || prefix.Length > 155)
					throw new NotSupportedException("Tar entry name too long: " + entry.Name);
			}

			WriteString(header, 0, 100, name);
			WriteString(header, 100, 8, "0000644\0");
			WriteString(header, 108, 8, "0000000\0");
			WriteString(header, 116, 8, "0000000\0");
			WriteOctal(header, 124, 12, size);

			long mtime = (long)(DateTime.UtcNow - Epoch).TotalSeconds;
			if (mtime < 0) mtime = 0;
			WriteOctal(header, 136, 12, mtime);

			for (int i = 148; i < 156; i++) header[i] = (byte)' ';

			header[156] = entry.TypeFlag == 0 ? (byte)'0' : entry.TypeFlag;

			WriteString(header, 257, 6, "ustar\0");
			header[263] = (byte)'0';
			header[264] = (byte)'0';

			WriteString(header, 345, 155, prefix);

			long sum = 0;
			for (int i = 0; i < 512; i++) sum += header[i];

			string checksumOctal = Convert.ToString(sum, 8).PadLeft(6, '0');
			byte[] csBytes = Encoding.ASCII.GetBytes(checksumOctal);
			for (int i = 0; i < 6; i++) header[148 + i] = csBytes[i];
			header[154] = 0;
			header[155] = (byte)' ';

			return header;
		}

		static void WriteString(byte[] buffer, int offset, int length, string value)
		{
			byte[] bytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
			int copy = Math.Min(bytes.Length, length);
			Array.Copy(bytes, 0, buffer, offset, copy);
			for (int i = copy; i < length; i++) buffer[offset + i] = 0;
		}

		static void WriteOctal(byte[] buffer, int offset, int length, long value)
		{
			int digits = length - 1;
			string s = Convert.ToString(value, 8).PadLeft(digits, '0');
			if (s.Length > digits)
				throw new NotSupportedException("Octal value does not fit");
			byte[] bytes = Encoding.ASCII.GetBytes(s);
			Array.Copy(bytes, 0, buffer, offset, digits);
			buffer[offset + digits] = 0;
		}
	}
}
