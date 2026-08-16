using System.Buffers.Binary;
using System.IO.Compression;

namespace DeadworksManaged.Api;

/// <summary>
/// Minimal PNG decoder — 8-bit RGB and RGBA, all five scanline filters — to
/// tightly-packed RGBA, so the server can turn a modder's <c>.png</c> into a
/// texture (<see cref="UiTexture"/>) with no image library. Deliberately small:
/// no palette, interlacing, 16-bit or grayscale, which UI art doesn't use.
/// </summary>
public static class UiPng
{
	private static readonly byte[] Magic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

	/// <summary>Decodes a PNG to row-major RGBA (top row first, 4 bytes/pixel).</summary>
	/// <exception cref="FormatException">Not a PNG, or an unsupported PNG variant.</exception>
	public static (byte[] Rgba, int Width, int Height) Decode(byte[] png)
	{
		ArgumentNullException.ThrowIfNull(png);
		if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Magic))
			throw new FormatException("Not a PNG (bad signature).");

		int width = 0, height = 0, channels = 0;
		var idat = new MemoryStream();
		int pos = 8;
		while (pos + 8 <= png.Length)
		{
			int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
			string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
			int body = pos + 8;
			if (body + len > png.Length)
				throw new FormatException("Truncated PNG chunk.");

			switch (type)
			{
				case "IHDR":
					width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(body));
					height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(body + 4));
					int bitDepth = png[body + 8];
					int colorType = png[body + 9];
					int interlace = png[body + 12];
					if (bitDepth != 8)
						throw new FormatException($"Only 8-bit PNGs are supported (got {bitDepth}-bit).");
					if (interlace != 0)
						throw new FormatException("Interlaced PNGs are not supported.");
					channels = colorType switch
					{
						2 => 3,   // RGB
						6 => 4,   // RGBA
						_ => throw new FormatException(
							$"Only RGB/RGBA PNGs are supported (colorType {colorType})."),
					};
					break;
				case "IDAT":
					idat.Write(png, body, len);
					break;
				case "IEND":
					pos = png.Length;
					continue;
			}
			pos = body + len + 4;   // skip body + CRC
		}

		if (width <= 0 || height <= 0 || channels == 0)
			throw new FormatException("PNG has no IHDR.");

		byte[] raw = Inflate(idat.ToArray());
		int stride = width * channels;
		if (raw.Length < (stride + 1) * height)
			throw new FormatException("PNG image data is short.");

		return (Defilter(raw, width, height, channels), width, height);
	}

	private static byte[] Inflate(byte[] zlib)
	{
		using var input = new MemoryStream(zlib);
		using var z = new ZLibStream(input, CompressionMode.Decompress);
		using var output = new MemoryStream();
		z.CopyTo(output);
		return output.ToArray();
	}

	/// <summary>Undoes per-scanline filtering and expands to RGBA.</summary>
	private static byte[] Defilter(byte[] raw, int width, int height, int channels)
	{
		int stride = width * channels;
		int bpp = channels;
		var cur = new byte[stride];
		var prev = new byte[stride];
		var rgba = new byte[width * height * 4];

		int src = 0;
		for (int y = 0; y < height; y++)
		{
			byte filter = raw[src++];
			for (int i = 0; i < stride; i++)
			{
				int x = raw[src++];
				int a = i >= bpp ? cur[i - bpp] : 0;
				int b = prev[i];
				int c = i >= bpp ? prev[i - bpp] : 0;
				int value = filter switch
				{
					0 => x,
					1 => x + a,
					2 => x + b,
					3 => x + ((a + b) >> 1),
					4 => x + Paeth(a, b, c),
					_ => throw new FormatException($"Bad PNG filter type {filter}."),
				};
				cur[i] = (byte)value;
			}

			// Expand this row into RGBA.
			int dst = y * width * 4;
			for (int p = 0; p < width; p++)
			{
				int s = p * channels;
				rgba[dst++] = cur[s];
				rgba[dst++] = cur[s + 1];
				rgba[dst++] = cur[s + 2];
				rgba[dst++] = channels == 4 ? cur[s + 3] : (byte)255;
			}

			(prev, cur) = (cur, prev);
		}
		return rgba;
	}

	private static int Paeth(int a, int b, int c)
	{
		int p = a + b - c;
		int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
		if (pa <= pb && pa <= pc) return a;
		return pb <= pc ? b : c;
	}
}
