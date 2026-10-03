using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using IphoneMover.Core;

namespace IphoneMover.Tests;

public sealed class FormatValidatorTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("iphonemover-tests").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private string SaveImage(string name, ImageFormat format)
    {
        string path = Path.Combine(dir, name);
        using var bmp = new Bitmap(64, 48);
        using (var g = Graphics.FromImage(bmp))
            g.Clear(Color.CornflowerBlue);
        bmp.Save(path, format);
        return path;
    }

    private static void Truncate(string path, int removeBytes)
    {
        using var fs = new FileStream(path, FileMode.Open);
        fs.SetLength(fs.Length - removeBytes);
    }

    private static byte[] Box(string type, byte[] payload)
    {
        var b = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        payload.CopyTo(b, 8);
        return b;
    }

    private string WriteBoxes(string name, params byte[][] boxes)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, boxes.SelectMany(b => b).ToArray());
        return path;
    }

    [Theory]
    [InlineData("a.jpg")]
    [InlineData("a.png")]
    [InlineData("a.gif")]
    [InlineData("a.tif")]
    public void RealImages_AreValid(string name)
    {
        var format = Path.GetExtension(name) switch
        {
            ".jpg" => ImageFormat.Jpeg, ".png" => ImageFormat.Png, ".gif" => ImageFormat.Gif, _ => ImageFormat.Tiff,
        };
        string path = SaveImage(name, format);
        var r = FormatValidator.Check(path, Path.GetExtension(name));
        Assert.True(r.Status == CheckStatus.Valid, r.ToString());
    }

    [Theory]
    [InlineData("b.jpg", 10)]
    [InlineData("b.png", 5)]
    [InlineData("b.gif", 1)]
    public void CutImages_AreInvalid(string name, int cut)
    {
        var format = Path.GetExtension(name) switch { ".jpg" => ImageFormat.Jpeg, ".png" => ImageFormat.Png, _ => ImageFormat.Gif };
        string path = SaveImage(name, format);
        Truncate(path, cut);
        Assert.Equal(CheckStatus.Invalid, FormatValidator.Check(path, Path.GetExtension(name)).Status);
    }

    [Fact]
    public void Png_WithBadCrc_IsInvalid()
    {
        string path = SaveImage("crc.png", ImageFormat.Png);
        byte[] data = File.ReadAllBytes(path);
        data[20] ^= 0xFF; // inside IHDR data
        File.WriteAllBytes(path, data);
        Assert.Equal(CheckStatus.Invalid, FormatValidator.Check(path, ".png").Status);
    }

    [Fact]
    public void Heic_GoodBoxes_IsValid()
    {
        string path = WriteBoxes("x.heic", Box("ftyp", Encoding.ASCII.GetBytes("heic\0\0\0\0mif1heic")),
            Box("meta", new byte[100]), Box("mdat", new byte[5000]));
        var r = FormatValidator.Check(path, ".HEIC");
        Assert.True(r.Status == CheckStatus.Valid, r.ToString());
    }

    [Fact]
    public void Heic_CutFile_IsInvalid()
    {
        string path = WriteBoxes("x.heic", Box("ftyp", new byte[12]), Box("meta", new byte[100]), Box("mdat", new byte[5000]));
        Truncate(path, 1);
        var r = FormatValidator.Check(path, ".heic");
        Assert.Equal(CheckStatus.Invalid, r.Status);
        Assert.Contains("mdat", r.Detail);
    }

    [Fact]
    public void Heic_ExtraBytes_IsInvalid()
    {
        string path = WriteBoxes("x.heic", Box("ftyp", new byte[12]), Box("meta", new byte[10]), new byte[3]);
        Assert.Equal(CheckStatus.Invalid, FormatValidator.Check(path, ".heic").Status);
    }

    [Fact]
    public void Mov_Valid_And_MissingMoov()
    {
        string ok = WriteBoxes("v.mov", Box("ftyp", new byte[12]), Box("wide", []), Box("mdat", new byte[2000]), Box("moov", new byte[300]));
        Assert.Equal(CheckStatus.Valid, FormatValidator.Check(ok, ".MOV").Status);

        string bad = WriteBoxes("w.mov", Box("ftyp", new byte[12]), Box("mdat", new byte[2000]));
        Assert.Equal(CheckStatus.Invalid, FormatValidator.Check(bad, ".mov").Status);
    }

    [Fact]
    public void Mp4_LargeSizeBox_IsValid()
    {
        // 64-bit size box: size field = 1, then 8-byte real size.
        var big = new byte[16 + 100];
        BinaryPrimitives.WriteUInt32BigEndian(big, 1);
        Encoding.ASCII.GetBytes("mdat").CopyTo(big, 4);
        BinaryPrimitives.WriteUInt64BigEndian(big.AsSpan(8), (ulong)big.Length);
        string path = WriteBoxes("l.mp4", Box("ftyp", new byte[12]), big, Box("moov", new byte[50]));
        Assert.Equal(CheckStatus.Valid, FormatValidator.Check(path, ".mp4").Status);
    }

    [Fact]
    public void Aae_Plist()
    {
        string path = Path.Combine(dir, "IMG_0001.AAE");
        File.WriteAllText(path, "<?xml version=\"1.0\"?>\n<plist version=\"1.0\"><dict/></plist>\n");
        Assert.Equal(CheckStatus.Valid, FormatValidator.Check(path, ".AAE").Status);
        File.WriteAllText(path, "<?xml version=\"1.0\"?>\n<plist version=\"1.0\"><di");
        Assert.Equal(CheckStatus.Invalid, FormatValidator.Check(path, ".AAE").Status);
    }

    [Fact]
    public void Webp_RiffSizeMustMatch()
    {
        var data = new byte[40];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)(data.Length - 8));
        Encoding.ASCII.GetBytes("WEBP").CopyTo(data, 8);
        string path = Path.Combine(dir, "a.webp");
        File.WriteAllBytes(path, data);
        Assert.Equal(CheckStatus.Valid, FormatValidator.Check(path, ".webp").Status);
        File.WriteAllBytes(path, data[..30]);
        Assert.Equal(CheckStatus.Invalid, FormatValidator.Check(path, ".webp").Status);
    }

    [Fact]
    public void UnknownExtension_IsNotSupported()
    {
        string path = Path.Combine(dir, "a.xyz");
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.Equal(CheckStatus.NotSupported, FormatValidator.Check(path, ".xyz").Status);
    }
}
