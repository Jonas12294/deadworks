using System.Buffers.Binary;
using System.Text;

namespace DeadworksManaged.Api;

/// <summary>
/// Builds a VPK v2 archive in memory from a set of named files — the container
/// Panorama mounts. Lets the server pack generated textures (<see cref="UiTexture"/>)
/// into a bundle with no external tool. Layout matches the reference packer
/// (<c>repack_vpk.py</c>) byte for byte.
/// </summary>
public static class UiVpk
{
	private const uint Signature = 0x55AA1234;

	/// <summary>
	/// Packs <paramref name="files"/> (forward-slash paths, e.g.
	/// <c>panorama/images/dw/icon.vtex_c</c>) into a single-archive VPK v2.
	/// </summary>
	/// <exception cref="ArgumentException">No files, or a file has an empty path.</exception>
	public static byte[] Build(IEnumerable<(string Path, byte[] Data)> files)
	{
		ArgumentNullException.ThrowIfNull(files);
		var list = files.ToList();
		if (list.Count == 0)
			throw new ArgumentException("A VPK needs at least one file.", nameof(files));

		// ext -> dir -> list of (name, crc, data), so the tree can be emitted
		// grouped exactly as the format requires.
		var byExt = new SortedDictionary<string, SortedDictionary<string, List<(string Name, uint Crc, byte[] Data)>>>(
			StringComparer.Ordinal);

		foreach (var (path, data) in list)
		{
			if (string.IsNullOrEmpty(path))
				throw new ArgumentException("A VPK file has an empty path.", nameof(files));
			ArgumentNullException.ThrowIfNull(data);

			int slash = path.LastIndexOf('/');
			string dir = slash >= 0 ? path[..slash] : " ";
			string filename = slash >= 0 ? path[(slash + 1)..] : path;
			int dot = filename.LastIndexOf('.');
			string name = dot >= 0 ? filename[..dot] : filename;
			string ext = dot >= 0 ? filename[(dot + 1)..] : "";

			if (!byExt.TryGetValue(ext, out var dirs))
				byExt[ext] = dirs = new SortedDictionary<string, List<(string, uint, byte[])>>(StringComparer.Ordinal);
			if (!dirs.TryGetValue(dir, out var names))
				dirs[dir] = names = new List<(string, uint, byte[])>();
			names.Add((name, Crc32(data), data));
		}

		var tree = new MemoryStream();
		var data2 = new MemoryStream();

		foreach (var (ext, dirs) in byExt)
		{
			WriteCString(tree, ext);
			foreach (var (dir, names) in dirs)
			{
				WriteCString(tree, dir);
				foreach (var (name, crc, bytes) in names.OrderBy(n => n.Name, StringComparer.Ordinal))
				{
					WriteCString(tree, name);
					Span<byte> entry = stackalloc byte[18];
					BinaryPrimitives.WriteUInt32LittleEndian(entry, crc);
					BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 0);          // preload bytes
					BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 0x7FFF);     // archive index: inline
					BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)data2.Length);
					BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)bytes.Length);
					BinaryPrimitives.WriteUInt16LittleEndian(entry[16..], 0xFFFF);    // terminator
					tree.Write(entry);
					data2.Write(bytes);
				}
				tree.WriteByte(0);   // end of names
			}
			tree.WriteByte(0);       // end of dirs
		}
		tree.WriteByte(0);           // end of extensions

		byte[] treeBytes = tree.ToArray();
		byte[] dataBytes = data2.ToArray();

		var vpk = new byte[12 + 16 + treeBytes.Length + dataBytes.Length];
		var span = vpk.AsSpan();
		BinaryPrimitives.WriteUInt32LittleEndian(span, Signature);
		BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 2);                       // version
		BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)treeBytes.Length);
		BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)dataBytes.Length); // file data size
		// archiveMd5 / otherMd5 / signature sizes all 0 (already zero-initialised)
		treeBytes.CopyTo(span[28..]);
		dataBytes.CopyTo(span[(28 + treeBytes.Length)..]);
		return vpk;
	}

	private static void WriteCString(Stream s, string text)
	{
		s.Write(Encoding.UTF8.GetBytes(text));
		s.WriteByte(0);
	}

	// IEEE CRC-32, matching zlib.crc32 (what the reference packer and the game use).
	private static uint Crc32(byte[] data)
	{
		uint crc = 0xFFFFFFFF;
		foreach (byte b in data)
		{
			crc ^= b;
			for (int i = 0; i < 8; i++)
				crc = (crc >> 1) ^ (0xEDB88320 & (uint)(-(int)(crc & 1)));
		}
		return crc ^ 0xFFFFFFFF;
	}
}
