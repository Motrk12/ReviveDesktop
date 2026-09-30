using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Revive.Core.IO;
using Revive.Core.Model;

namespace Revive.Core.Carving;

/// <summary>JPEG: walks the marker segments and the compressed scan data up to the End Of Image marker.</summary>
public sealed class JpegCarver : IFileCarver
{
    private const long MaxSize = 200L * 1024 * 1024;

    public IReadOnlyList<byte> LeadBytes { get; } = [0xFF];

    public bool Matches(ReadOnlySpan<byte> h) =>
        h.Length >= 4 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF && h[3] is (>= 0xE0 and <= 0xEF) or 0xDB or 0xC0 or 0xC2 or 0xC4 or 0xFE;

    public CarveResult? Carve(SourceReader r, long offset)
    {
        long p = offset + 2;
        long limit = Math.Min(r.Length, offset + MaxSize);
        bool frame = false, scan = false;
        DateTime? taken = null;

        while (p < limit)
        {
            if (r.U8(p) != 0xFF)
                return null;
            byte marker = r.U8(p + 1);
            p += 2;
            while (marker == 0xFF)
            {
                marker = r.U8(p);
                p++;
            }

            if (marker == 0xD9)
                return frame && scan ? new CarveResult(p - offset, "jpg", FileCategory.Photo, taken) : null;
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
                continue;
            if (marker is 0x00 or 0xD8)
                return null;

            int length = r.U16BE(p);
            if (length < 2)
                return null;
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                frame = true;
            if (marker == 0xE1 && taken is null && length > 8)
                taken = Exif.TryReadDateTaken(r, p + 2, length - 2);

            p += length;
            if (marker == 0xDA)
            {
                if (!frame)
                    return null;
                scan = true;
                p = SkipEntropyData(r, p, limit);
                if (p < 0)
                    return null;
            }
        }
        return null;
    }

    /// <summary>Returns the offset of the next real marker after compressed data (FF00 and restart markers are data).</summary>
    private static long SkipEntropyData(SourceReader r, long p, long limit)
    {
        var buffer = new byte[64 * 1024];
        while (p < limit)
        {
            int count = (int)Math.Min(buffer.Length, Math.Min(limit, r.Length) - p);
            if (count < 2)
                return -1;
            r.Read(p, buffer.AsSpan(0, count));
            for (int i = 0; i < count - 1; i++)
            {
                if (buffer[i] != 0xFF)
                    continue;
                byte next = buffer[i + 1];
                if (next == 0x00 || next is >= 0xD0 and <= 0xD7 || next == 0xFF)
                    continue;
                return p + i;
            }
            p += count - 1;
        }
        return -1;
    }
}

/// <summary>PNG: follows the chunk list up to IEND.</summary>
public sealed class PngCarver : IFileCarver
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public IReadOnlyList<byte> LeadBytes { get; } = [0x89];

    public bool Matches(ReadOnlySpan<byte> h) =>
        h.Length >= 16 && h.StartsWith(Signature) && h.Slice(12, 4).SequenceEqual("IHDR"u8);

    public CarveResult? Carve(SourceReader r, long offset)
    {
        long p = offset + 8;
        Span<byte> type = stackalloc byte[4];
        for (int chunks = 0; chunks < 1_000_000; chunks++)
        {
            uint length = r.U32BE(p);
            if (length > 0x7FFFFFFF)
                return null;
            r.Read(p + 4, type);
            foreach (byte b in type)
                if (!char.IsAsciiLetter((char)b))
                    return null;
            p += 12 + length;
            if (type.SequenceEqual("IEND"u8))
                return new CarveResult(p - offset, "png", FileCategory.Photo);
        }
        return null;
    }
}

/// <summary>GIF: walks image and extension blocks up to the trailer.</summary>
public sealed class GifCarver : IFileCarver
{
    public IReadOnlyList<byte> LeadBytes { get; } = [(byte)'G'];

    public bool Matches(ReadOnlySpan<byte> h) => h.Length >= 13 && (h.StartsWith("GIF89a"u8) || h.StartsWith("GIF87a"u8));

    public CarveResult? Carve(SourceReader r, long offset)
    {
        long p = offset + 6;
        byte flags = r.U8(p + 4);
        p += 7;
        if ((flags & 0x80) != 0)
            p += 3 * (1 << ((flags & 7) + 1));

        for (int blocks = 0; blocks < 1_000_000; blocks++)
        {
            byte kind = r.U8(p);
            switch (kind)
            {
                case 0x2C: // image
                {
                    byte local = r.U8(p + 9);
                    p += 10;
                    if ((local & 0x80) != 0)
                        p += 3 * (1 << ((local & 7) + 1));
                    p = SkipSubBlocks(r, p + 1);
                    break;
                }
                case 0x21: // extension
                    p = SkipSubBlocks(r, p + 2);
                    break;
                case 0x3B:
                    return new CarveResult(p + 1 - offset, "gif", FileCategory.Photo);
                default:
                    return null;
            }
        }
        return null;
    }

    private static long SkipSubBlocks(SourceReader r, long p)
    {
        while (true)
        {
            byte size = r.U8(p++);
            if (size == 0)
                return p;
            p += size;
        }
    }
}

/// <summary>BMP: the header states the total file size.</summary>
public sealed class BmpCarver : IFileCarver
{
    public IReadOnlyList<byte> LeadBytes { get; } = [(byte)'B'];

    public bool Matches(ReadOnlySpan<byte> h)
    {
        if (h.Length < 30 || h[0] != 'B' || h[1] != 'M')
            return false;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(h[2..]);
        uint reserved = BinaryPrimitives.ReadUInt32LittleEndian(h[6..]);
        uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(h[10..]);
        uint dib = BinaryPrimitives.ReadUInt32LittleEndian(h[14..]);
        ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(h[28..]);
        return reserved == 0 && dib is 12 or 40 or 52 or 56 or 64 or 108 or 124
            && dataOffset >= 14 + dib && dataOffset < size && size < 512 * 1024 * 1024
            && bits is 1 or 4 or 8 or 16 or 24 or 32;
    }

    public CarveResult? Carve(SourceReader r, long offset) =>
        new(r.U32LE(offset + 2), "bmp", FileCategory.Photo);
}

/// <summary>TIFF and TIFF-based camera RAW (CR2, NEF, ARW, DNG, PEF): the end is the furthest data any IFD points to.</summary>
public sealed class TiffCarver : IFileCarver
{
    private const long MaxSize = 300L * 1024 * 1024;

    public IReadOnlyList<byte> LeadBytes { get; } = [(byte)'I', (byte)'M'];

    public bool Matches(ReadOnlySpan<byte> h) =>
        h.Length >= 16 && (h.StartsWith("II*\0"u8) || h.StartsWith("MM\0*"u8));

    public CarveResult? Carve(SourceReader r, long offset)
    {
        var tiff = new TiffReader(r, offset, r.U8(offset) == 'I');
        uint first = tiff.U32(4);
        if (first < 8 || first > MaxSize)
            return null;

        long end = 8;
        string? make = null;
        bool dng = false;
        var queue = new Queue<uint>([first]);
        var seen = new HashSet<uint>();
        int ifds = 0;

        while (queue.Count > 0 && ifds++ < 64)
        {
            uint ifd = queue.Dequeue();
            if (!seen.Add(ifd) || ifd > MaxSize)
                continue;
            int count = tiff.U16(ifd);
            if (count == 0 || count > 1000)
            {
                if (ifd == first)
                    return null;
                continue;
            }
            end = Math.Max(end, ifd + 2 + count * 12L + 4);

            uint[]? stripOffsets = null, stripCounts = null;
            for (int i = 0; i < count; i++)
            {
                long e = ifd + 2 + i * 12L;
                int tag = tiff.U16(e);
                int type = tiff.U16(e + 2);
                uint n = tiff.U32(e + 4);
                int unit = TiffReader.TypeSize(type);
                if (unit == 0)
                {
                    if (ifd == first)
                        return null;
                    continue;
                }

                long bytes = unit * (long)n;
                if (bytes > 4)
                {
                    uint at = tiff.U32(e + 8);
                    if (at + bytes <= MaxSize)
                        end = Math.Max(end, at + bytes);
                }

                switch (tag)
                {
                    case 0x010F when bytes > 4 && bytes < 64:
                        make = tiff.Ascii(tiff.U32(e + 8), (int)n);
                        break;
                    case 0x010F:
                        make = tiff.Ascii(e + 8, (int)n);
                        break;
                    case 0xC612:
                        dng = true;
                        break;
                    case 0x8769 or 0x014A:
                        foreach (uint sub in tiff.Values(e, type, n))
                            queue.Enqueue(sub);
                        break;
                    case 0x0111 or 0x0144 or 0x0201:
                        stripOffsets = tiff.Values(e, type, n);
                        break;
                    case 0x0117 or 0x0145 or 0x0202:
                        stripCounts = tiff.Values(e, type, n);
                        break;
                }
            }

            if (stripOffsets is not null && stripCounts is not null)
                for (int i = 0; i < Math.Min(stripOffsets.Length, stripCounts.Length); i++)
                    if ((long)stripOffsets[i] + stripCounts[i] <= MaxSize)
                        end = Math.Max(end, (long)stripOffsets[i] + stripCounts[i]);

            uint next = tiff.U32(ifd + 2 + count * 12L);
            if (next != 0)
                queue.Enqueue(next);
        }

        if (end < 1024 || end > r.Length - offset)
            return null;

        string ext = r.Matches(offset + 8, "CR"u8) ? "cr2"
            : dng ? "dng"
            : make?.StartsWith("NIKON", StringComparison.OrdinalIgnoreCase) == true ? "nef"
            : make?.StartsWith("SONY", StringComparison.OrdinalIgnoreCase) == true ? "arw"
            : make?.StartsWith("PENTAX", StringComparison.OrdinalIgnoreCase) == true ? "pef"
            : "tif";
        return new CarveResult(end, ext, FileCategory.Photo);
    }
}

/// <summary>Reads TIFF structures (also used for EXIF inside JPEG).</summary>
internal readonly struct TiffReader(SourceReader reader, long start, bool littleEndian)
{
    public ushort U16(long at) => littleEndian ? reader.U16LE(start + at) : reader.U16BE(start + at);

    public uint U32(long at) => littleEndian ? reader.U32LE(start + at) : reader.U32BE(start + at);

    public string Ascii(long at, int count)
    {
        count = Math.Min(count, 64);
        return Encoding.ASCII.GetString(reader.ReadBytes(start + at, count)).TrimEnd('\0', ' ');
    }

    public uint[] Values(long entry, int type, uint count)
    {
        count = Math.Min(count, 4096);
        int unit = TypeSize(type);
        long at = unit * count > 4 ? U32(entry + 8) : entry + 8;
        var values = new uint[count];
        for (int i = 0; i < count; i++)
            values[i] = unit == 2 ? U16(at + i * 2L) : U32(at + i * 4L);
        return values;
    }

    public static int TypeSize(int type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 => 8,
        _ => 0,
    };
}

internal static class Exif
{
    /// <summary>Reads DateTimeOriginal (or DateTime) from an APP1 "Exif" segment.</summary>
    public static DateTime? TryReadDateTaken(SourceReader r, long segment, int length)
    {
        try
        {
            if (length < 14 || !r.Matches(segment, "Exif\0\0"u8))
                return null;
            long start = segment + 6;
            byte order = r.U8(start);
            if (order is not ((byte)'I' or (byte)'M'))
                return null;
            var tiff = new TiffReader(r, start, order == 'I');

            uint ifd0 = tiff.U32(4);
            if (ifd0 >= length)
                return null;
            string? fallback = null;
            uint exifIfd = 0;
            int count = tiff.U16(ifd0);
            for (int i = 0; i < count && i < 200; i++)
            {
                long e = ifd0 + 2 + i * 12L;
                int tag = tiff.U16(e);
                if (tag == 0x0132)
                    fallback = tiff.Ascii(tiff.U32(e + 8), 19);
                else if (tag == 0x8769)
                    exifIfd = tiff.U32(e + 8);
            }

            if (exifIfd > 0 && exifIfd < length)
            {
                count = tiff.U16(exifIfd);
                for (int i = 0; i < count && i < 200; i++)
                {
                    long e = exifIfd + 2 + i * 12L;
                    if (tiff.U16(e) == 0x9003)
                        return Parse(tiff.Ascii(tiff.U32(e + 8), 19)) ?? Parse(fallback);
                }
            }
            return Parse(fallback);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    private static DateTime? Parse(string? value) =>
        value is not null && DateTime.TryParseExact(value, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date)
            ? date
            : null;
}
