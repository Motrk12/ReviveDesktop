using System.Text;
using Revive.Core.IO;
using Revive.Core.Model;

namespace Revive.Core.Carving;

/// <summary>PDF: ends at the last "%%EOF" that isn't followed by an incremental update.</summary>
public sealed class PdfCarver : IFileCarver
{
    private const long MaxSize = 500L * 1024 * 1024;

    public IReadOnlyList<byte> LeadBytes { get; } = [(byte)'%'];

    public bool Matches(ReadOnlySpan<byte> h) => h.Length >= 8 && (h.StartsWith("%PDF-1."u8) || h.StartsWith("%PDF-2."u8));

    public CarveResult? Carve(SourceReader r, long offset)
    {
        long limit = Math.Min(r.Length, offset + MaxSize);
        long search = offset + 8;
        while (true)
        {
            long eof = r.IndexOf("%%EOF"u8, search, limit);
            if (eof < 0)
                return null;
            long end = eof + 5;
            while (end < limit && r.U8(end) is (byte)'\r' or (byte)'\n' or (byte)' ')
                end++;
            if (!LooksLikeMorePdf(r, end, limit))
                return new CarveResult(end - offset, "pdf", FileCategory.Document);
            search = end;
        }
    }

    /// <summary>Incremental saves and linearized files carry on after a %%EOF with more objects.</summary>
    private static bool LooksLikeMorePdf(SourceReader r, long p, long limit)
    {
        if (p + 16 > limit)
            return false;
        string next = Encoding.ASCII.GetString(r.ReadBytes(p, 16));
        if (next.StartsWith("xref", StringComparison.Ordinal) || next.StartsWith("%PDF", StringComparison.Ordinal))
            return next.StartsWith("xref", StringComparison.Ordinal);
        int i = 0;
        while (i < next.Length && char.IsAsciiDigit(next[i]))
            i++;
        if (i == 0 || i >= next.Length || next[i] != ' ')
            return false;
        int j = i + 1;
        while (j < next.Length && char.IsAsciiDigit(next[j]))
            j++;
        return j > i + 1 && next[j..].StartsWith(" obj", StringComparison.Ordinal);
    }
}

/// <summary>
/// ZIP and formats built on it (DOCX, XLSX, PPTX, ODT, EPUB, APK): ends at the End Of Central Directory record
/// whose offsets point back to this file's start.
/// </summary>
public sealed class ZipCarver : IFileCarver
{
    private const long MaxSize = 2L * 1024 * 1024 * 1024;

    public IReadOnlyList<byte> LeadBytes { get; } = [(byte)'P'];

    public bool Matches(ReadOnlySpan<byte> h) =>
        h.Length >= 30 && h[0] == 'P' && h[1] == 'K' && h[2] == 3 && h[3] == 4 && h[5] == 0 && (h[26] | h[27]) != 0;

    public CarveResult? Carve(SourceReader r, long offset)
    {
        long limit = Math.Min(r.Length, offset + MaxSize);
        long search = FindCentralDirectory(r, offset, limit);
        if (search < 0)
            return null;
        while (true)
        {
            long eocd = r.IndexOf("PK\x05\x06"u8, search, limit);
            if (eocd < 0)
                return null;
            search = eocd + 4;
            if (eocd + 22 > r.Length)
                return null;

            uint cdSize = r.U32LE(eocd + 12);
            uint cdOffset = r.U32LE(eocd + 16);
            if (cdOffset + (long)cdSize != eocd - offset || !r.Matches(offset + cdOffset, "PK\x01\x02"u8))
                continue; // belongs to a zip stored inside this one

            int comment = r.U16LE(eocd + 20);
            var (ext, category) = Identify(r, offset, offset + cdOffset, cdSize);
            return new CarveResult(eocd + 22 + comment - offset, ext, category);
        }
    }

    /// <summary>
    /// Hops over the local file entries (their sizes are usually recorded up front) to reach the central
    /// directory, so a damaged zip is rejected quickly instead of searching far ahead for an end record.
    /// Returns where to start looking for the end record, or -1 if the entries don't chain together.
    /// </summary>
    private static long FindCentralDirectory(SourceReader r, long offset, long limit)
    {
        long p = offset;
        for (int entries = 0; entries < 200_000; entries++)
        {
            if (p + 30 > limit)
                return -1;
            if (!r.Matches(p, "PK\x03\x04"u8))
                return r.Matches(p, "PK\x01\x02"u8) || r.Matches(p, "PK\x05\x06"u8) ? p : -1;

            int flags = r.U16LE(p + 6);
            uint compressed = r.U32LE(p + 18);
            if ((flags & 0x08) != 0 || compressed == 0xFFFFFFFF)
                return p + 30; // size is only stored after the data: fall back to searching from here
            p += 30L + r.U16LE(p + 26) + r.U16LE(p + 28) + compressed;
        }
        return -1;
    }

    private static (string, FileCategory) Identify(SourceReader r, long start, long cd, uint cdSize)
    {
        var names = new List<string>();
        long p = cd;
        while (p + 46 <= cd + cdSize && names.Count < 2000 && r.Matches(p, "PK\x01\x02"u8))
        {
            int nameLength = r.U16LE(p + 28);
            int extra = r.U16LE(p + 30);
            int comment = r.U16LE(p + 32);
            names.Add(Encoding.UTF8.GetString(r.ReadBytes(p + 46, Math.Min(nameLength, 512))));
            p += 46 + nameLength + extra + comment;
        }

        if (names.Any(n => n.StartsWith("word/", StringComparison.Ordinal)))
            return ("docx", FileCategory.Document);
        if (names.Any(n => n.StartsWith("xl/", StringComparison.Ordinal)))
            return ("xlsx", FileCategory.Document);
        if (names.Any(n => n.StartsWith("ppt/", StringComparison.Ordinal)))
            return ("pptx", FileCategory.Document);
        if (names.Contains("AndroidManifest.xml"))
            return ("apk", FileCategory.Other);

        if (names.Count > 0 && names[0] == "mimetype")
        {
            // ODF and EPUB store their type, uncompressed, as the first entry.
            int nameLength = r.U16LE(start + 26);
            int extra = r.U16LE(start + 28);
            string mime = Encoding.ASCII.GetString(r.ReadBytes(start + 30 + nameLength + extra, 64));
            if (mime.StartsWith("application/epub+zip", StringComparison.Ordinal))
                return ("epub", FileCategory.Document);
            if (mime.StartsWith("application/vnd.oasis.opendocument.text", StringComparison.Ordinal))
                return ("odt", FileCategory.Document);
            if (mime.StartsWith("application/vnd.oasis.opendocument.spreadsheet", StringComparison.Ordinal))
                return ("ods", FileCategory.Document);
            if (mime.StartsWith("application/vnd.oasis.opendocument.presentation", StringComparison.Ordinal))
                return ("odp", FileCategory.Document);
        }
        if (names.Contains("META-INF/MANIFEST.MF"))
            return ("jar", FileCategory.Archive);
        return ("zip", FileCategory.Archive);
    }
}

/// <summary>
/// Legacy Office files (DOC, XLS, PPT, MSG) use the OLE compound file format. Its size follows from the
/// highest sector its allocation table uses.
/// </summary>
public sealed class OleCarver : IFileCarver
{
    private const uint FreeSector = 0xFFFFFFFF;
    private const uint EndOfChain = 0xFFFFFFFE;
    private static readonly byte[] Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public IReadOnlyList<byte> LeadBytes { get; } = [0xD0];

    public bool Matches(ReadOnlySpan<byte> h) =>
        h.Length >= 512 && h.StartsWith(Signature) && h[0x1C] == 0xFE && h[0x1D] == 0xFF && h[0x1E] is 9 or 12;

    public CarveResult? Carve(SourceReader r, long offset)
    {
        int sectorSize = 1 << r.U8(offset + 0x1E);
        long SectorOffset(uint id) => offset + (id + 1L) * sectorSize;

        uint fatSectors = r.U32LE(offset + 0x2C);
        uint firstDir = r.U32LE(offset + 0x30);
        uint difat = r.U32LE(offset + 0x44);
        if (fatSectors == 0 || fatSectors > 100_000)
            return null;

        var fatIds = new List<uint>();
        for (int i = 0; i < 109 && fatIds.Count < fatSectors; i++)
        {
            uint id = r.U32LE(offset + 0x4C + i * 4);
            if (id == FreeSector)
                break;
            fatIds.Add(id);
        }
        int perSector = sectorSize / 4;
        for (int guard = 0; difat is not (FreeSector or EndOfChain) && fatIds.Count < fatSectors && guard < 10_000; guard++)
        {
            for (int i = 0; i < perSector - 1 && fatIds.Count < fatSectors; i++)
                fatIds.Add(r.U32LE(SectorOffset(difat) + i * 4));
            difat = r.U32LE(SectorOffset(difat) + (perSector - 1) * 4);
        }

        long highest = -1;
        var fat = new List<uint>(fatIds.Count * perSector);
        foreach (uint fatId in fatIds)
        {
            byte[] sector = r.ReadBytes(SectorOffset(fatId), sectorSize);
            for (int i = 0; i < perSector; i++)
            {
                uint value = BitConverter.ToUInt32(sector, i * 4);
                fat.Add(value);
                if (value != FreeSector)
                    highest = fat.Count - 1;
            }
        }
        if (highest < 0)
            return null;

        long length = (highest + 2) * sectorSize;
        var (ext, category) = Identify(r, fat, firstDir, sectorSize, SectorOffset);
        return new CarveResult(length, ext, category);
    }

    private static (string, FileCategory) Identify(SourceReader r, List<uint> fat, uint dir, int sectorSize, Func<uint, long> sectorOffset)
    {
        var names = new HashSet<string>();
        for (int n = 0; n < 32 && dir < fat.Count; n++)
        {
            for (int e = 0; e < sectorSize / 128; e++)
            {
                long at = sectorOffset(dir) + e * 128;
                int nameBytes = Math.Min((int)r.U16LE(at + 0x40), 64);
                if (nameBytes >= 2)
                    names.Add(Encoding.Unicode.GetString(r.ReadBytes(at, nameBytes - 2)));
            }
            dir = fat[(int)dir];
        }

        if (names.Contains("WordDocument"))
            return ("doc", FileCategory.Document);
        if (names.Contains("Workbook") || names.Contains("Book"))
            return ("xls", FileCategory.Document);
        if (names.Contains("PowerPoint Document"))
            return ("ppt", FileCategory.Document);
        if (names.Contains("__nameid_version1.0") || names.Any(n => n.StartsWith("__substg1.0_", StringComparison.Ordinal)))
            return ("msg", FileCategory.Document);
        if (names.Contains("VisioDocument"))
            return ("vsd", FileCategory.Document);
        return ("ole", FileCategory.Other);
    }
}

/// <summary>7-Zip: the start header records where the archive's closing header is.</summary>
public sealed class SevenZipCarver : IFileCarver
{
    private static readonly byte[] Signature = [(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C];

    public IReadOnlyList<byte> LeadBytes { get; } = [(byte)'7'];

    public bool Matches(ReadOnlySpan<byte> h) =>
        h.Length >= 32 && h.StartsWith(Signature) && Crc32.Compute(h.Slice(12, 20)) == BitConverter.ToUInt32(h.Slice(8, 4));

    public CarveResult? Carve(SourceReader r, long offset)
    {
        ulong nextOffset = r.U64LE(offset + 12);
        ulong nextSize = r.U64LE(offset + 20);
        if (nextSize == 0 || nextSize > 1 << 30 || nextOffset > 1UL << 40)
            return null;
        return new CarveResult(32 + (long)nextOffset + (long)nextSize, "7z", FileCategory.Archive);
    }
}

internal static class Crc32
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
