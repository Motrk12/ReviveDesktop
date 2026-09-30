using System.Text;
using Revive.Core.IO;
using Revive.Core.Model;

namespace Revive.Core.Carving;

/// <summary>
/// ISO base media files (MP4, MOV, M4A, 3GP, HEIC, AVIF, CR3): a list of top-level boxes, each with its own size.
/// </summary>
public sealed class IsoMediaCarver : IFileCarver
{
    private static readonly HashSet<string> TopLevelBoxes =
    [
        "ftyp", "moov", "mdat", "free", "skip", "wide", "uuid", "meta", "moof", "mfra", "sidx", "ssix",
        "styp", "pdin", "udta", "prft", "emsg", "pnot", "junk", "idat", "iinf", "iloc", "iprp", "pitm",
    ];

    public IReadOnlyList<byte> LeadBytes { get; } = [0x00];

    public bool Matches(ReadOnlySpan<byte> h)
    {
        if (h.Length < 16 || h[0] != 0 || !h.Slice(4, 4).SequenceEqual("ftyp"u8))
            return false;
        uint size = (uint)(h[0] << 24 | h[1] << 16 | h[2] << 8 | h[3]);
        return size is >= 16 and <= 1024 && IsPrintable(h.Slice(8, 4));
    }

    public CarveResult? Carve(SourceReader r, long offset)
    {
        string brand = Encoding.ASCII.GetString(r.ReadBytes(offset + 8, 4));
        long p = offset;
        bool moov = false, mdat = false, meta = false, moof = false;
        Span<byte> type = stackalloc byte[4];

        for (int boxes = 0; boxes < 100_000 && p + 8 <= r.Length; boxes++)
        {
            long size = r.U32BE(p);
            r.Read(p + 4, type);
            string name = Encoding.ASCII.GetString(type);
            if (!TopLevelBoxes.Contains(name))
                break;
            if (size == 1)
                size = (long)r.U64BE(p + 8);
            else if (size == 0)
                size = r.Length - p; // "extends to end of file": take what's left
            if (size < 8)
                break;

            moov |= name == "moov";
            mdat |= name == "mdat";
            meta |= name == "meta";
            moof |= name == "moof";
            p += size;
        }

        if (!(mdat && (moov || meta || moof)))
            return null;

        long length = Math.Min(p, r.Length) - offset;
        var (ext, category) = brand switch
        {
            "qt  " => ("mov", FileCategory.Video),
            "heic" or "heix" or "hevc" or "heim" or "heis" or "mif1" or "msf1" => ("heic", FileCategory.Photo),
            "avif" or "avis" => ("avif", FileCategory.Photo),
            "crx " => ("cr3", FileCategory.Photo),
            "M4A " or "M4B " or "M4P " => ("m4a", FileCategory.Audio),
            "M4V " or "M4VH" or "M4VP" => ("m4v", FileCategory.Video),
            _ when brand.StartsWith("3g", StringComparison.Ordinal) => ("3gp", FileCategory.Video),
            _ => ("mp4", FileCategory.Video),
        };
        return new CarveResult(length, ext, category);
    }

    private static bool IsPrintable(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
            if (b is < 0x20 or > 0x7E)
                return false;
        return true;
    }
}

/// <summary>RIFF containers: AVI (including OpenDML extensions over 1 GB), WAV and WebP.</summary>
public sealed class RiffCarver : IFileCarver
{
    public IReadOnlyList<byte> LeadBytes { get; } = [(byte)'R'];

    public bool Matches(ReadOnlySpan<byte> h)
    {
        if (h.Length < 16 || !h.StartsWith("RIFF"u8))
            return false;
        var form = h.Slice(8, 4);
        var first = h.Slice(12, 4);
        return (form.SequenceEqual("AVI "u8) && first.SequenceEqual("LIST"u8))
            || (form.SequenceEqual("WAVE"u8) && (first.SequenceEqual("fmt "u8) || first.SequenceEqual("JUNK"u8) || first.SequenceEqual("bext"u8)))
            || (form.SequenceEqual("WEBP"u8) && first[..3].SequenceEqual("VP8"u8));
    }

    public CarveResult? Carve(SourceReader r, long offset)
    {
        string form = Encoding.ASCII.GetString(r.ReadBytes(offset + 8, 4));
        long p = offset + 8 + r.U32LE(offset + 4);
        if (p - offset < 20)
            return null;

        if (form == "AVI ")
        {
            // Files over 1 GB continue in extra "RIFF....AVIX" chunks.
            while (p + 12 <= r.Length && r.Matches(p, "RIFF"u8) && r.Matches(p + 8, "AVIX"u8))
                p += 8 + r.U32LE(p + 4);
        }

        var (ext, category) = form switch
        {
            "AVI " => ("avi", FileCategory.Video),
            "WAVE" => ("wav", FileCategory.Audio),
            _ => ("webp", FileCategory.Photo),
        };
        return new CarveResult(p + (p & 1) - offset, ext, category);
    }
}

/// <summary>Matroska and WebM: an EBML header followed by one Segment element.</summary>
public sealed class MatroskaCarver : IFileCarver
{
    private const uint SegmentId = 0x18538067;
    private static readonly HashSet<uint> SegmentChildren =
        [0x114D9B74, 0x1549A966, 0x1654AE6B, 0x1F43B675, 0x1C53BB6B, 0x1043A770, 0x1254C367, 0x1941A469, 0xEC, 0xBF];

    public IReadOnlyList<byte> LeadBytes { get; } = [0x1A];

    public bool Matches(ReadOnlySpan<byte> h) => h.Length >= 8 && h[0] == 0x1A && h[1] == 0x45 && h[2] == 0xDF && h[3] == 0xA3;

    public CarveResult? Carve(SourceReader r, long offset)
    {
        var (_, headerSize, headerLength) = ReadElement(r, offset);
        if (headerSize < 0 || headerSize > 4096)
            return null;
        long bodyStart = offset + headerLength;
        string docType = FindDocType(r, bodyStart, bodyStart + headerSize) ?? "";
        if (docType is not ("webm" or "matroska"))
            return null;

        long p = bodyStart + headerSize;
        var (id, segmentSize, segmentHeader) = ReadElement(r, p);
        if (id != SegmentId)
            return null;

        long end;
        if (segmentSize >= 0)
            end = p + segmentHeader + segmentSize;
        else
        {
            // Unknown size (live recordings): walk the segment's children instead.
            end = p + segmentHeader;
            while (end + 2 <= r.Length)
            {
                var (childId, childSize, childHeader) = ReadElement(r, end);
                if (!SegmentChildren.Contains(childId) || childSize < 0)
                    break;
                end += childHeader + childSize;
            }
        }

        string ext = docType == "webm" ? "webm" : "mkv";
        return new CarveResult(Math.Min(end, r.Length) - offset, ext, FileCategory.Video);
    }

    private static string? FindDocType(SourceReader r, long p, long end)
    {
        while (p < end)
        {
            var (id, size, header) = ReadElement(r, p);
            if (size < 0)
                return null;
            if (id == 0x4282)
                return Encoding.ASCII.GetString(r.ReadBytes(p + header, (int)Math.Min(size, 32))).TrimEnd('\0');
            p += header + size;
        }
        return null;
    }

    /// <summary>Reads an element ID and size (size is -1 when "unknown").</summary>
    private static (uint Id, long Size, int HeaderLength) ReadElement(SourceReader r, long p)
    {
        byte first = r.U8(p);
        int idLength = first >= 0x80 ? 1 : first >= 0x40 ? 2 : first >= 0x20 ? 3 : first >= 0x10 ? 4 : 0;
        if (idLength == 0)
            return (0, -1, 0);
        uint id = 0;
        for (int i = 0; i < idLength; i++)
            id = (id << 8) | r.U8(p + i);

        byte sizeFirst = r.U8(p + idLength);
        int sizeLength = 1;
        while (sizeLength <= 8 && (sizeFirst & (0x80 >> (sizeLength - 1))) == 0)
            sizeLength++;
        if (sizeLength > 8)
            return (id, -1, 0);

        long size = sizeFirst & (0xFF >> sizeLength);
        bool allOnes = size == (0xFF >> sizeLength);
        for (int i = 1; i < sizeLength; i++)
        {
            byte b = r.U8(p + idLength + i);
            size = (size << 8) | b;
            allOnes &= b == 0xFF;
        }
        return (id, allOnes ? -1 : size, idLength + sizeLength);
    }
}

/// <summary>Ogg (Vorbis, Opus, Theora): a chain of pages, each with its own length.</summary>
public sealed class OggCarver : IFileCarver
{
    public IReadOnlyList<byte> LeadBytes { get; } = [(byte)'O'];

    public bool Matches(ReadOnlySpan<byte> h) => h.Length >= 64 && h.StartsWith("OggS"u8) && h[4] == 0 && (h[5] & 0x02) != 0;

    public CarveResult? Carve(SourceReader r, long offset)
    {
        int segments = r.U8(offset + 26);
        byte[] payload = r.ReadBytes(offset + 27 + segments, 8);
        long p = offset;
        int pages = 0;
        while (p + 27 <= r.Length && r.Matches(p, "OggS"u8) && r.U8(p + 4) == 0)
        {
            int count = r.U8(p + 26);
            byte[] table = r.ReadBytes(p + 27, count);
            p += 27 + count + table.Sum(b => (long)b);
            pages++;
        }
        if (pages < 2)
            return null;

        var (ext, category) = payload switch
        {
            [0x01, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s', ..] => ("ogg", FileCategory.Audio),
            [(byte)'O', (byte)'p', (byte)'u', (byte)'s', (byte)'H', (byte)'e', (byte)'a', (byte)'d'] => ("opus", FileCategory.Audio),
            [0x80, (byte)'t', (byte)'h', (byte)'e', (byte)'o', (byte)'r', (byte)'a', ..] => ("ogv", FileCategory.Video),
            _ => ("ogg", FileCategory.Audio),
        };
        return new CarveResult(p - offset, ext, category);
    }
}

/// <summary>MP3 with an ID3v2 tag: skips the tag, then walks MPEG audio frames.</summary>
public sealed class Mp3Carver : IFileCarver
{
    private static readonly int[,] Bitrates =
    {
        // MPEG-1 layer I, II, III; MPEG-2/2.5 layer I, II/III (kbit/s)
        { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 },
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 },
        { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 },
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 },
        { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },
    };

    private static readonly int[,] SampleRates = { { 11025, 12000, 8000 }, { 0, 0, 0 }, { 22050, 24000, 16000 }, { 44100, 48000, 32000 } };

    public IReadOnlyList<byte> LeadBytes { get; } = [(byte)'I'];

    public bool Matches(ReadOnlySpan<byte> h) =>
        h.Length >= 10 && h.StartsWith("ID3"u8) && h[3] is >= 2 and <= 4 && h[4] != 0xFF
        && h[6] < 0x80 && h[7] < 0x80 && h[8] < 0x80 && h[9] < 0x80;

    public CarveResult? Carve(SourceReader r, long offset)
    {
        long tagSize = (r.U8(offset + 6) << 21) | (r.U8(offset + 7) << 14) | (r.U8(offset + 8) << 7) | r.U8(offset + 9);
        long p = offset + 10 + tagSize + ((r.U8(offset + 5) & 0x10) != 0 ? 10 : 0);

        // Some encoders pad the tag with extra zeros.
        for (int i = 0; i < 4096 && p < r.Length && r.U8(p) == 0; i++)
            p++;

        int frames = 0;
        while (p + 4 <= r.Length)
        {
            int length = FrameLength(r.U32BE(p));
            if (length <= 0)
                break;
            p += length;
            frames++;
        }
        if (frames < 8)
            return null;

        if (p + 128 <= r.Length && r.Matches(p, "TAG"u8))
            p += 128;
        return new CarveResult(Math.Min(p, r.Length) - offset, "mp3", FileCategory.Audio);
    }

    private static int FrameLength(uint header)
    {
        if ((header & 0xFFE00000) != 0xFFE00000)
            return 0;
        int version = (int)(header >> 19) & 3;   // 0 = 2.5, 2 = 2, 3 = 1
        int layer = (int)(header >> 17) & 3;     // 1 = III, 2 = II, 3 = I
        int bitrateIndex = (int)(header >> 12) & 0xF;
        int rateIndex = (int)(header >> 10) & 3;
        int padding = (int)(header >> 9) & 1;
        if (version == 1 || layer == 0 || bitrateIndex is 0 or 15 || rateIndex == 3)
            return 0;

        int row = version == 3 ? 3 - layer : layer == 3 ? 3 : 4;
        int bitrate = Bitrates[row, bitrateIndex] * 1000;
        int sampleRate = SampleRates[version, rateIndex];
        if (layer == 3)
            return (12 * bitrate / sampleRate + padding) * 4;
        return (version == 3 || layer == 2 ? 144 : 72) * bitrate / sampleRate + padding;
    }
}
