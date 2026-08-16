using System.Buffers.Binary;

namespace DeadworksManaged.Api;

/// <summary>
/// Encodes raw RGBA pixels into a Source 2 compiled texture (<c>.vtex_c</c>) —
/// the only image form Panorama panels can load (they rewrite every src to a
/// compiled resource; loose and raw files are never read, proven live
/// 2026-08-14). This lets a server turn a PNG into a mountable texture with no
/// CSDK and no resource compiler.
/// </summary>
/// <remarks>
/// <para>The compiler emits the hard variant (DXT1, mip chain, compressed
/// storage); this emits the simplest one the engine still accepts:
/// <c>RGBA8888</c>, a single mip, no extra-data — so the image is stored raw
/// and there is no block compressor or mip generator to get wrong.</para>
/// <para>Container: 16-byte resource header, a one-entry block table, and the
/// <c>DATA</c> block (a 40-byte <c>VTexExtraData</c> header + the pixels).
/// No <c>RERL</c>/<c>RED2</c> introspection — the vtex DATA block is a fixed
/// struct the loader reads directly.</para>
/// </remarks>
public static class UiTexture
{
	/// <summary>VTEX_FORMAT_RGBA8888 — uncompressed 8-bit RGBA.</summary>
	private const byte FormatRgba8888 = 4;

	/// <summary>VTEX_FLAG_NO_LOD (0x8) — single mip, no LOD chain.</summary>
	private const ushort FlagNoLod = 0x8;

	/// <summary>
	/// Builds a <c>.vtex_c</c> from tightly-packed RGBA bytes (row-major, top
	/// row first, 4 bytes per pixel).
	/// </summary>
	/// <exception cref="ArgumentException">Dimensions are non-positive or don't match the buffer length.</exception>
	public static byte[] EncodeVtex(byte[] rgba, int width, int height)
	{
		ArgumentNullException.ThrowIfNull(rgba);
		if (width <= 0 || height <= 0)
			throw new ArgumentException($"Texture dimensions must be positive (got {width}x{height}).");
		if (rgba.Length != width * height * 4)
			throw new ArgumentException(
				$"RGBA buffer is {rgba.Length} bytes; {width}x{height} needs {width * height * 4}.");

		const int headerSize = 16;      // fileSize, headerVer, resVer, blockOffset, blockCount
		const int blockEntry = 12;      // "DATA" + relOffset + size
		const int dataHeader = 40;      // VTexExtraData fixed header

		int dataStart = headerSize + blockEntry;        // 28
		int resourceEnd = dataStart + dataHeader;       // 68 — where the DATA block ends
		int total = resourceEnd + rgba.Length;          // image is appended past the resource
		var buf = new byte[total];
		var span = buf.AsSpan();

		// --- resource header ---
		// fileSize is the resource's declared end (the DATA block end); the
		// appended image sits past it, exactly as the compiler emits.
		BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)resourceEnd);
		BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 12);               // headerVersion
		BinaryPrimitives.WriteUInt16LittleEndian(span[6..], 1);                // resourceVersion
		BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 8);                // blockOffset (rel to +8 → table at 16)
		BinaryPrimitives.WriteUInt32LittleEndian(span[12..], 1);               // blockCount

		// --- block table: one DATA entry at offset 16 ---
		"DATA"u8.CopyTo(span[16..]);
		// relOffset is measured from the relOffset field itself (offset 20).
		BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)(dataStart - 20));
		BinaryPrimitives.WriteUInt32LittleEndian(span[24..], dataHeader);      // block size excludes the appended image

		// --- DATA: VTexExtraData header ---
		var data = span[dataStart..];
		BinaryPrimitives.WriteUInt16LittleEndian(data, 1);                     // version
		BinaryPrimitives.WriteUInt16LittleEndian(data[2..], FlagNoLod);        // flags
		// reflectivity[4] left at 0 — a lighting fallback, irrelevant to a HUD image.
		BinaryPrimitives.WriteUInt16LittleEndian(data[20..], (ushort)width);
		BinaryPrimitives.WriteUInt16LittleEndian(data[22..], (ushort)height);
		BinaryPrimitives.WriteUInt16LittleEndian(data[24..], 1);               // depth
		data[26] = FormatRgba8888;
		data[27] = 1;                                                          // numMipLevels
		BinaryPrimitives.WriteUInt32LittleEndian(data[28..], 0);               // picmip0 packed res
		BinaryPrimitives.WriteUInt32LittleEndian(data[32..], 8);               // extraDataOffset (unused, count 0)
		BinaryPrimitives.WriteUInt32LittleEndian(data[36..], 0);               // extraDataCount

		// Image data appended after the DATA block (past the declared end).
		rgba.CopyTo(span[resourceEnd..]);
		return buf;
	}
}
