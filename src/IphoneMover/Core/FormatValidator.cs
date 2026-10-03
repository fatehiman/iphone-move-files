using System.Buffers.Binary;
using System.Text;

namespace IphoneMover.Core;

internal enum CheckStatus
{
    Valid,
    Invalid,
    NotSupported,
}

internal readonly record struct CheckResult(CheckStatus Status, string Detail)
{
    public static CheckResult Ok(string detail) => new(CheckStatus.Valid, detail);
    public static CheckResult Bad(string detail) => new(CheckStatus.Invalid, detail);
    public override string ToString() => $"{Status}: {Detail}";
}

/// <summary>
/// Checks that a copied file has a healthy structure for its format: the header is right,
/// and the sizes written inside the file match the real file size.
/// </summary>
internal static class FormatValidator
{
    private static readonly HashSet<string> IsoBmffImage = new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif", ".hif", ".avif" };
    private static readonly HashSet<string> IsoBmffVideo = new(StringComparer.OrdinalIgnoreCase) { ".mov", ".mp4", ".m4v", ".3gp", ".qt" };

    public static bool IsSupported(string extension) =>
        IsoBmffImage.Contains(extension) || IsoBmffVideo.Contains(extension) || extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".tif" or ".tiff" or ".dng" or ".webp" or ".avi" or ".aae" => true,
            _ => false,
        };

    /// <param name="path">File to check.</param>
    /// <param name="extension">The real extension (the file on disk may still be named *.part).</param>
    public static CheckResult Check(string path, string extension)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            if (fs.Length == 0)
                return CheckResult.Bad("file is empty");

            if (IsoBmffImage.Contains(extension))
                return CheckIsoBmff(fs, video: false);
            if (IsoBmffVideo.Contains(extension))
                return CheckIsoBmff(fs, video: true);

            return extension.ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => CheckJpeg(fs),
                ".png" => CheckPng(fs),
                ".gif" => CheckGif(fs),
                ".tif" or ".tiff" or ".dng" => CheckTiff(fs),
                ".webp" => CheckRiff(fs, "WEBP"),
                ".avi" => CheckRiff(fs, "AVI "),
                ".aae" => CheckAae(fs),
                _ => new CheckResult(CheckStatus.NotSupported, $"no format check for '{extension}'"),
            };
        }
        catch (IOException ex)
        {
            return CheckResult.Bad("read error: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ ISO BMFF (HEIC, MOV, MP4)

    /// <summary>
    /// Walks the top level boxes. Each box header has its own length; the lengths must add up
    /// to exactly the file length. This is the "size in header = real size" check.
    /// </summary>
    private static CheckResult CheckIsoBmff(FileStream fs, bool video)
    {
        long len = fs.Length;
        long pos = 0;
        var types = new List<string>();
        Span<byte> hdr = stackalloc byte[16];

        while (pos < len)
        {
            if (len - pos < 8)
                return CheckResult.Bad($"{len - pos} extra bytes at end of file (not a full box header)");

            fs.Position = pos;
            ReadExactly(fs, hdr[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            string type = Encoding.ASCII.GetString(hdr.Slice(4, 4));
            if (!IsBoxType(hdr.Slice(4, 4)))
                return CheckResult.Bad($"bad box type at offset {pos}");

            int headerSize = 8;
            if (size == 1)
            {
                if (len - pos < 16)
                    return CheckResult.Bad($"box '{type}' has a truncated 64-bit size");
                ReadExactly(fs, hdr.Slice(8, 8));
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.Slice(8, 8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = len - pos; // box runs to end of file
            }

            if (size < headerSize)
                return CheckResult.Bad($"box '{type}' at offset {pos} has invalid size {size}");
            if (size > len - pos)
                return CheckResult.Bad($"box '{type}' says {size:N0} bytes but only {len - pos:N0} bytes are left (file is cut)");

            types.Add(type);
            pos += size;
        }

        if (types.Count == 0)
            return CheckResult.Bad("no boxes");

        if (video)
        {
            if (!types.Contains("moov"))
                return CheckResult.Bad("video has no 'moov' box");
            if (!types.Contains("mdat") && !types.Contains("moof"))
                return CheckResult.Bad("video has no media data ('mdat')");
        }
        else
        {
            if (types[0] != "ftyp")
                return CheckResult.Bad("image does not start with 'ftyp'");
            if (!types.Contains("meta"))
                return CheckResult.Bad("image has no 'meta' box");
        }

        return CheckResult.Ok($"{types.Count} boxes ({string.Join(",", types.Distinct())}), sizes add up to {len:N0} bytes");
    }

    private static bool IsBoxType(ReadOnlySpan<byte> t)
    {
        foreach (byte b in t)
            if (b < 0x20 || b > 0x7E)
                return false;
        return true;
    }

    // ------------------------------------------------------------------ JPEG

    private static CheckResult CheckJpeg(FileStream fs)
    {
        long len = fs.Length;
        if (len < 4)
            return CheckResult.Bad("too short");
        fs.Position = 0;
        if (fs.ReadByte() != 0xFF || fs.ReadByte() != 0xD8)
            return CheckResult.Bad("missing JPEG start marker (FFD8)");

        bool sawFrame = false;
        long pos = 2;
        while (true)
        {
            fs.Position = pos;
            int b = fs.ReadByte();
            if (b != 0xFF)
                return CheckResult.Bad($"expected marker at offset {pos}");
            int marker;
            do { marker = fs.ReadByte(); } while (marker == 0xFF);
            if (marker < 0)
                return CheckResult.Bad("file ends inside the header");
            pos = fs.Position;

            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                continue; // no length
            if (marker == 0xD9)
                return CheckResult.Bad("end marker before image data");

            if (len - pos < 2)
                return CheckResult.Bad("file ends inside the header");
            int segLen = (fs.ReadByte() << 8) | fs.ReadByte();
            if (segLen < 2)
                return CheckResult.Bad($"bad segment length at offset {pos}");
            if (pos + segLen > len)
                return CheckResult.Bad($"segment FF{marker:X2} says {segLen} bytes but the file is shorter (file is cut)");

            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                sawFrame = true;
            pos += segLen;
            if (marker == 0xDA)
                break; // start of scan: compressed data follows
        }

        if (!sawFrame)
            return CheckResult.Bad("no frame header (SOF)");

        // The file must end with the EOI marker (FFD9). Allow zero padding after it.
        long end = len;
        var tail = new byte[(int)Math.Min(len - pos, 4096)];
        fs.Position = len - tail.Length;
        ReadExactly(fs, tail);
        int i = tail.Length - 1;
        while (i >= 0 && tail[i] == 0x00)
            i--;
        if (i < 1 || tail[i - 1] != 0xFF || tail[i] != 0xD9)
            return CheckResult.Bad("missing JPEG end marker (FFD9): file is probably cut");
        end = len - (tail.Length - 1 - i);

        return CheckResult.Ok($"JPEG structure OK, ends with FFD9 at {end:N0}");
    }

    // ------------------------------------------------------------------ PNG

    private static CheckResult CheckPng(FileStream fs)
    {
        byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        long len = fs.Length;
        var head = new byte[8];
        if (len < 8)
            return CheckResult.Bad("too short");
        fs.Position = 0;
        ReadExactly(fs, head);
        if (!head.AsSpan().SequenceEqual(sig))
            return CheckResult.Bad("bad PNG signature");

        long pos = 8;
        int chunks = 0;
        Span<byte> h = stackalloc byte[8];
        Span<byte> crcBytes = stackalloc byte[4];
        var buf = new byte[64 * 1024];
        while (true)
        {
            if (len - pos < 12)
                return CheckResult.Bad("file ends inside a chunk (file is cut)");
            fs.Position = pos;
            ReadExactly(fs, h);
            long dataLen = BinaryPrimitives.ReadUInt32BigEndian(h);
            string type = Encoding.ASCII.GetString(h.Slice(4, 4));
            if (chunks == 0 && type != "IHDR")
                return CheckResult.Bad("first chunk is not IHDR");
            if (pos + 12 + dataLen > len)
                return CheckResult.Bad($"chunk '{type}' says {dataLen:N0} bytes but the file is shorter (file is cut)");

            // CRC covers type + data.
            uint crc = Crc32.Start;
            crc = Crc32.Update(crc, h.Slice(4, 4));
            long left = dataLen;
            while (left > 0)
            {
                int n = (int)Math.Min(left, buf.Length);
                ReadExactly(fs, buf.AsSpan(0, n));
                crc = Crc32.Update(crc, buf.AsSpan(0, n));
                left -= n;
            }
            ReadExactly(fs, crcBytes);
            if (Crc32.Finish(crc) != BinaryPrimitives.ReadUInt32BigEndian(crcBytes))
                return CheckResult.Bad($"CRC error in chunk '{type}'");

            chunks++;
            pos += 12 + dataLen;
            if (type == "IEND")
                break;
        }

        if (pos != len)
            return CheckResult.Bad($"{len - pos:N0} extra bytes after IEND");
        return CheckResult.Ok($"PNG OK, {chunks} chunks, all CRCs match");
    }

    // ------------------------------------------------------------------ GIF / TIFF / RIFF / AAE

    private static CheckResult CheckGif(FileStream fs)
    {
        if (fs.Length < 14)
            return CheckResult.Bad("too short");
        var head = new byte[6];
        fs.Position = 0;
        ReadExactly(fs, head);
        string sig = Encoding.ASCII.GetString(head);
        if (sig != "GIF87a" && sig != "GIF89a")
            return CheckResult.Bad("bad GIF signature");
        fs.Position = fs.Length - 1;
        if (fs.ReadByte() != 0x3B)
            return CheckResult.Bad("missing GIF trailer (file is probably cut)");
        return CheckResult.Ok("GIF header and trailer OK");
    }

    private static CheckResult CheckTiff(FileStream fs)
    {
        long len = fs.Length;
        if (len < 16)
            return CheckResult.Bad("too short");
        var head = new byte[8];
        fs.Position = 0;
        ReadExactly(fs, head);
        bool le;
        if (head[0] == 'I' && head[1] == 'I' && head[2] == 42 && head[3] == 0) le = true;
        else if (head[0] == 'M' && head[1] == 'M' && head[2] == 0 && head[3] == 42) le = false;
        else return CheckResult.Bad("bad TIFF/DNG header");

        uint U32(ReadOnlySpan<byte> b) => le ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b);
        ushort U16(ReadOnlySpan<byte> b) => le ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b);

        long ifd = U32(head.AsSpan(4));
        int ifds = 0;
        var two = new byte[2];
        var four = new byte[4];
        while (ifd != 0)
        {
            if (ifd < 8 || ifd + 2 > len)
                return CheckResult.Bad($"IFD offset {ifd} is outside the file (file is cut)");
            fs.Position = ifd;
            ReadExactly(fs, two);
            int entries = U16(two);
            long next = ifd + 2 + entries * 12L;
            if (next + 4 > len)
                return CheckResult.Bad("IFD runs past end of file (file is cut)");
            fs.Position = next;
            ReadExactly(fs, four);
            ifd = U32(four);
            if (++ifds > 64)
                return CheckResult.Bad("too many IFDs (loop?)");
        }
        return CheckResult.Ok($"TIFF/DNG header OK, {ifds} IFD(s) inside the file");
    }

    private static CheckResult CheckRiff(FileStream fs, string form)
    {
        if (fs.Length < 12)
            return CheckResult.Bad("too short");
        var head = new byte[12];
        fs.Position = 0;
        ReadExactly(fs, head);
        if (Encoding.ASCII.GetString(head, 0, 4) != "RIFF" || Encoding.ASCII.GetString(head, 8, 4) != form)
            return CheckResult.Bad($"bad RIFF/{form.Trim()} header");
        long declared = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)) + 8L;
        long padded = declared + (declared & 1);
        if (declared != fs.Length && padded != fs.Length)
            return CheckResult.Bad($"header says {declared:N0} bytes, file has {fs.Length:N0}");
        return CheckResult.Ok($"RIFF size in header matches file size ({fs.Length:N0})");
    }

    private static CheckResult CheckAae(FileStream fs)
    {
        // .AAE is the iPhone edit sidecar: a property list (XML or binary).
        var head = new byte[(int)Math.Min(fs.Length, 64)];
        fs.Position = 0;
        ReadExactly(fs, head);
        string s = Encoding.ASCII.GetString(head).TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (s.StartsWith("bplist00", StringComparison.Ordinal))
            return CheckResult.Ok("binary plist");
        if (s.StartsWith("<?xml", StringComparison.Ordinal) || s.StartsWith("<plist", StringComparison.Ordinal))
        {
            fs.Position = 0;
            using var reader = new StreamReader(fs, leaveOpen: true);
            string text = reader.ReadToEnd();
            return text.TrimEnd().EndsWith("</plist>", StringComparison.Ordinal)
                ? CheckResult.Ok("XML plist")
                : CheckResult.Bad("XML plist is not complete (file is cut)");
        }
        return CheckResult.Bad("not a property list");
    }

    private static void ReadExactly(Stream s, Span<byte> buffer)
    {
        s.ReadExactly(buffer);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();
        public const uint Start = 0xFFFFFFFF;

        private static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte b in data)
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        public static uint Finish(uint crc) => crc ^ 0xFFFFFFFF;
    }
}
