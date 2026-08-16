using System.Buffers.Binary;
using DeadworksManaged.Api;
using Xunit;

namespace DeadworksManaged.Tests;

public class UiTextureTests
{
    /// <summary>Reads back the Source 2 resource header and the vtex DATA block,
    /// mirroring how the engine parses it — the real check that the container
    /// is well-formed.</summary>
    private static (int W, int H, int Format, int Mips, byte[] Image) ParseVtex(byte[] vtex)
    {
        uint blockOff = BinaryPrimitives.ReadUInt32LittleEndian(vtex.AsSpan(8));
        uint blockCount = BinaryPrimitives.ReadUInt32LittleEndian(vtex.AsSpan(12));
        Assert.Equal((ushort)12, BinaryPrimitives.ReadUInt16LittleEndian(vtex.AsSpan(4)));   // headerVersion

        int arrayBase = 8 + (int)blockOff;
        int dataOff = -1, dataSize = 0;
        for (int i = 0; i < blockCount; i++)
        {
            int e = arrayBase + i * 12;
            string type = System.Text.Encoding.ASCII.GetString(vtex, e, 4);
            uint rel = BinaryPrimitives.ReadUInt32LittleEndian(vtex.AsSpan(e + 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(vtex.AsSpan(e + 8));
            if (type == "DATA") { dataOff = e + 4 + (int)rel; dataSize = (int)size; }
        }
        Assert.True(dataOff > 0, "DATA block present");

        int w = BinaryPrimitives.ReadUInt16LittleEndian(vtex.AsSpan(dataOff + 20));
        int h = BinaryPrimitives.ReadUInt16LittleEndian(vtex.AsSpan(dataOff + 22));
        int fmt = vtex[dataOff + 26];
        int mips = vtex[dataOff + 27];
        uint extraCount = BinaryPrimitives.ReadUInt32LittleEndian(vtex.AsSpan(dataOff + 36));

        // The image is appended after the DATA block (Source 2 stores texture
        // pixels past the resource's declared end); the loader finds it at
        // DATA_offset + DATA_size.
        int imageStart = dataOff + dataSize;
        Assert.Equal(0u, extraCount);
        var image = vtex[imageStart..(imageStart + w * h * 4)];
        return (w, h, fmt, mips, image);
    }

    [Fact]
    public void EncodesRgbaAsAWellFormedUncompressedVtex()
    {
        // 2x2, one solid colour per texel (RGBA order).
        byte[] rgba =
        {
            255, 0, 0, 255,     0, 255, 0, 255,
            0, 0, 255, 255,     255, 255, 255, 255,
        };

        byte[] vtex = UiTexture.EncodeVtex(rgba, 2, 2);
        var (w, h, fmt, mips, image) = ParseVtex(vtex);

        Assert.Equal(2, w);
        Assert.Equal(2, h);
        Assert.Equal(4, fmt);           // VTEX_FORMAT_RGBA8888
        Assert.Equal(1, mips);          // single mip, no chain to encode
        Assert.Equal(rgba, image);      // pixels survive verbatim

        // fileSize is the resource's declared end (the DATA block end); the
        // image is appended past it, matching the compiler's own layout.
        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(vtex);
        Assert.Equal((uint)(vtex.Length - rgba.Length), declared);
    }

    [Fact]
    public void RejectsAMismatchedPixelBuffer()
    {
        Assert.Throws<ArgumentException>(() => UiTexture.EncodeVtex(new byte[10], 2, 2));
        Assert.Throws<ArgumentException>(() => UiTexture.EncodeVtex(new byte[16], 0, 2));
    }
}

public class UiPngTests
{
    // 4x4 RGBA, rows filtered None/Sub/Up/Average; ground truth is the raw RGBA.
    private const string RgbaPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAYAAACp8Z5+AAAAMklEQVR4nGNgYGD4b8Mg97WCweb1Foaoh4wMNnIggW8wzAQUYEDGzAwVNg1ycnK/YRgAeoIRxyXE2KYAAAAASUVORK5CYII=";
    private const string RgbaGt =
        "AAAA/zwAHvV4ADzrtABa4QA8Hv88PDz1eDxa67Q8eOEAeDz/PHha9Xh4eOu0eJbhALRa/zy0ePV4tJbrtLS04Q==";

    // 3x2 RGB, rows filtered Paeth/Sub; ground truth expands to RGBA (alpha 255).
    private const string RgbPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAMAAAACCAIAAAASFvFNAAAAE0lEQVR4nGPhEpGDAMaUvAoICwAawgL0x1Uj5AAAAABJRU5ErkJggg==";
    private const string RgbGt = "ChQe/ygyPP9GUFr/ZG54/4KMlv+gqrT/";

    [Fact]
    public void DecodesRgbaWithEveryScanlineFilter()
    {
        var (rgba, w, h) = UiPng.Decode(Convert.FromBase64String(RgbaPng));
        Assert.Equal(4, w);
        Assert.Equal(4, h);
        Assert.Equal(Convert.FromBase64String(RgbaGt), rgba);
    }

    [Fact]
    public void DecodesRgbAndExpandsToOpaqueRgba()
    {
        var (rgba, w, h) = UiPng.Decode(Convert.FromBase64String(RgbPng));
        Assert.Equal(3, w);
        Assert.Equal(2, h);
        Assert.Equal(Convert.FromBase64String(RgbGt), rgba);
    }

    [Fact]
    public void RejectsNonPngInput()
    {
        Assert.Throws<FormatException>(() => UiPng.Decode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
        Assert.Throws<FormatException>(() => UiPng.Decode(Array.Empty<byte>()));
    }
}

public class UiImagePackTests
{
    // A 4x4 RGBA PNG (reused from the decoder fixtures).
    private const string RgbaPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAYAAACp8Z5+AAAAMklEQVR4nGNgYGD4b8Mg97WCweb1Foaoh4wMNnIggW8wzAQUYEDGzAwVNg1ycnK/YRgAeoIRxyXE2KYAAAAASUVORK5CYII=";

    /// <summary>Finds one file's bytes in a VPK v2 (single archive, inline).</summary>
    private static byte[]? ReadFromVpk(byte[] vpk, string wantPath)
    {
        int treeSize = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(vpk.AsSpan(8));
        int dataStart = 28 + treeSize;
        int pos = 28;
        string ReadCStr()
        {
            int end = Array.IndexOf(vpk, (byte)0, pos);
            string s = System.Text.Encoding.UTF8.GetString(vpk, pos, end - pos);
            pos = end + 1;
            return s;
        }
        while (true)
        {
            string ext = ReadCStr();
            if (ext.Length == 0) break;
            while (true)
            {
                string dir = ReadCStr();
                if (dir.Length == 0) break;
                while (true)
                {
                    string name = ReadCStr();
                    if (name.Length == 0) break;
                    uint offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(vpk.AsSpan(pos + 8));
                    uint length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(vpk.AsSpan(pos + 12));
                    pos += 18;
                    string full = dir == " " ? $"{name}.{ext}" : $"{dir}/{name}.{ext}";
                    if (full == wantPath)
                        return vpk[(dataStart + (int)offset)..(dataStart + (int)offset + (int)length)];
                }
            }
        }
        return null;
    }

    [Fact]
    public void BuildsAMountableImagesVpkAndMapsEachNameToItsS2rPath()
    {
        var pack = UiImagePack.Build(new Dictionary<string, byte[]>
        {
            ["icon"] = Convert.FromBase64String(RgbaPng),
        });

        // The panel-facing path a tree/markup src uses.
        Assert.Equal("s2r://panorama/images/dw/icon.vtex", pack.Paths["icon"]);

        // The VPK really contains a compiled texture at the matching path...
        byte[]? vtex = ReadFromVpk(pack.Vpk, "panorama/images/dw/icon.vtex_c");
        Assert.NotNull(vtex);

        // ...and it is exactly what the encoder makes from the decoded pixels.
        var (rgba, w, h) = UiPng.Decode(Convert.FromBase64String(RgbaPng));
        Assert.Equal(UiTexture.EncodeVtex(rgba, w, h), vtex);
    }

    [Fact]
    public void RejectsAnEmptySetOrABadName()
    {
        Assert.Throws<ArgumentException>(() => UiImagePack.Build(new Dictionary<string, byte[]>()));
        Assert.Throws<ArgumentException>(() => UiImagePack.Build(new Dictionary<string, byte[]>
        {
            ["../evil"] = Convert.FromBase64String(RgbaPng),
        }));
    }
}

public class UiVpkTests
{
    // Produced by the reference repack_vpk.py (build_vpk_tree + write_vpk) for
    // exactly the three files below - the C# writer must match it byte for
    // byte, which also proves it mounts (Python-built VPKs are proven live).
    private const string Golden =
        "NBKqVQIAAABkAAAAEgAAAAAAAAAAAAAAAAAAAHR4dAAgAHJvb3QA+/k8aQAA/38AAAAACQAAAP//AAB2" +
        "dGV4X2MAcGFub3JhbWEvaW1hZ2VzL2R3AGEAFjZEMgAA/38JAAAABwAAAP//YgDEH0QbAAD/fxAAAAAC" +
        "AAAA//8AAAByb290LWRhdGFoZWxsby1BQkI=";

    [Fact]
    public void BuildsAVpkByteIdenticalToTheReferencePacker()
    {
        byte[] vpk = UiVpk.Build(new (string, byte[])[]
        {
            ("panorama/images/dw/a.vtex_c", System.Text.Encoding.ASCII.GetBytes("hello-A")),
            ("panorama/images/dw/b.vtex_c", System.Text.Encoding.ASCII.GetBytes("BB")),
            ("root.txt", System.Text.Encoding.ASCII.GetBytes("root-data")),
        });

        Assert.Equal(Convert.FromBase64String(Golden), vpk);
    }

    [Fact]
    public void EmitsAValidV2HeaderRegardlessOfInput()
    {
        byte[] vpk = UiVpk.Build(new (string, byte[])[] { ("a/b.vtex_c", new byte[] { 1, 2, 3 }) });

        Assert.Equal(0x55AA1234u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(vpk));
        Assert.Equal(2u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(vpk.AsSpan(4)));
    }

    [Fact]
    public void RejectsEmptyOrPathlessInput()
    {
        Assert.Throws<ArgumentException>(() => UiVpk.Build(Array.Empty<(string, byte[])>()));
        Assert.Throws<ArgumentException>(() => UiVpk.Build(new (string, byte[])[] { ("", new byte[1]) }));
    }
}
