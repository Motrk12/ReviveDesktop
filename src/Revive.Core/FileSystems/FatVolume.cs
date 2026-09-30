using System.Buffers.Binary;
using System.Text;
using Revive.Core.IO;
using Revive.Core.Model;

namespace Revive.Core.FileSystems;

/// <summary>
/// Finds deleted files on FAT12/16/32 (USB sticks, older SD cards). Deleting a file overwrites the first
/// letter of its short name with 0xE5 and frees its cluster chain, but the start cluster and size survive.
/// </summary>
public sealed class FatVolume : IFileSystemScanner
{
    private const int MaxDirectoryClusters = 65536;

    private readonly IDiskSource _source;
    private readonly int _bytesPerSector;
    private readonly long _clusterSize;
    private readonly long _rootDirOffset;
    private readonly int _rootEntryCount;
    private readonly long _dataOffset;
    private readonly long _clusterCount;
    private readonly uint _rootCluster;
    private readonly int _fatBits;
    private readonly byte[] _fat;
    private readonly HashSet<uint> _visited = [];

    private FatVolume(IDiskSource source, int bytesPerSector, long clusterSize, long rootDirOffset, int rootEntryCount,
        long dataOffset, long clusterCount, uint rootCluster, int fatBits, byte[] fat)
    {
        _source = source;
        _bytesPerSector = bytesPerSector;
        _clusterSize = clusterSize;
        _rootDirOffset = rootDirOffset;
        _rootEntryCount = rootEntryCount;
        _dataOffset = dataOffset;
        _clusterCount = clusterCount;
        _rootCluster = rootCluster;
        _fatBits = fatBits;
        _fat = fat;
    }

    public string Name => $"FAT{_fatBits}";

    public static FatVolume? TryOpen(IDiskSource source)
    {
        byte[] boot = source.ReadBytes(0, 512);
        if (boot[0] is not (0xEB or 0xE9) || boot[510] != 0x55 || boot[511] != 0xAA)
            return null;
        string oem = Encoding.ASCII.GetString(boot, 3, 8);
        if (oem is "NTFS    " or "EXFAT   ")
            return null;

        int bytesPerSector = U16(boot, 0x0B);
        int sectorsPerCluster = boot[0x0D];
        int reserved = U16(boot, 0x0E);
        int fatCount = boot[0x10];
        int rootEntryCount = U16(boot, 0x11);
        long totalSectors = U16(boot, 0x13);
        if (totalSectors == 0)
            totalSectors = U32(boot, 0x20);
        long fatSize = U16(boot, 0x16);
        if (fatSize == 0)
            fatSize = U32(boot, 0x24);

        if (bytesPerSector is not (512 or 1024 or 2048 or 4096) || sectorsPerCluster == 0
            || (sectorsPerCluster & (sectorsPerCluster - 1)) != 0 || reserved == 0 || fatCount is < 1 or > 2 || fatSize == 0)
            return null;

        long rootDirSectors = (rootEntryCount * 32L + bytesPerSector - 1) / bytesPerSector;
        long firstDataSector = reserved + fatCount * fatSize + rootDirSectors;
        if (firstDataSector >= totalSectors)
            return null;
        long clusterCount = (totalSectors - firstDataSector) / sectorsPerCluster;
        int fatBits = clusterCount < 4085 ? 12 : clusterCount < 65525 ? 16 : 32;
        if (fatBits == 32 && rootEntryCount != 0)
            return null;

        long fatBytes = fatSize * bytesPerSector;
        if (fatBytes > 512L * 1024 * 1024)
            return null;
        byte[] fat = source.ReadBytes((long)reserved * bytesPerSector, (int)fatBytes);

        uint rootCluster = fatBits == 32 ? U32(boot, 0x2C) : 0;
        return new FatVolume(source, bytesPerSector, (long)sectorsPerCluster * bytesPerSector,
            (reserved + fatCount * fatSize) * bytesPerSector, rootEntryCount, firstDataSector * bytesPerSector,
            clusterCount, rootCluster, fatBits, fat);
    }

    public void FindDeleted(Action<FoundFile> found, IProgress<double>? progress, CancellationToken ct)
    {
        _visited.Clear();
        var pending = new Stack<(byte[] Data, string Path, bool Deleted)>();

        byte[] root = _fatBits == 32
            ? ReadChain(_rootCluster)
            : _source.ReadBytes(_rootDirOffset, _rootEntryCount * 32);
        if (_fatBits == 32)
            _visited.Add(_rootCluster);
        pending.Push((root, @"\", false));

        int processed = 0;
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (data, path, deleted) = pending.Pop();
            foreach (var entry in GuessLostFirstLetters(ParseDirectory(data, deleted).ToList()))
            {
                if (entry.IsDirectory)
                {
                    if (!IsValidCluster(entry.FirstCluster) || !_visited.Add(entry.FirstCluster))
                        continue;
                    byte[] contents = entry.Deleted ? ReadDeletedDirectory(entry.FirstCluster) : ReadChain(entry.FirstCluster);
                    pending.Push((contents, CombinePath(path, entry.Name), entry.Deleted));
                }
                else if (entry.Deleted && entry.Size > 0 && IsValidCluster(entry.FirstCluster))
                {
                    var file = ToFoundFile(entry, path);
                    if (file is not null)
                        found(file);
                }
            }
            processed++;
            progress?.Report(Math.Min(0.99, processed / (processed + pending.Count + 1.0)));
        }
        progress?.Report(1);
    }

    public IAllocationMap LoadAllocationMap()
    {
        var bits = new byte[(_clusterCount + 7) / 8];
        for (long i = 0; i < _clusterCount; i++)
            if (GetFatEntry((uint)(i + 2)) != 0)
                bits[i >> 3] |= (byte)(1 << (int)(i & 7));
        return new ClusterAllocationMap(bits, _clusterCount, _dataOffset, _clusterSize);
    }

    private FoundFile? ToFoundFile(FatEntry entry, string path)
    {
        // The chain is gone once a file is deleted, so assume the clusters were contiguous (they usually are).
        long clusters = (entry.Size + _clusterSize - 1) / _clusterSize;
        if (entry.FirstCluster + clusters - 1 > _clusterCount + 1)
            return null;

        bool reused = false;
        for (long c = 0; c < clusters && !reused; c++)
            reused = GetFatEntry((uint)(entry.FirstCluster + c)) != 0;

        long offset = ClusterOffset(entry.FirstCluster);
        return new FoundFile
        {
            Name = entry.Name,
            FolderPath = path,
            Size = entry.Size,
            Modified = entry.Modified,
            Category = FileTypes.FromFileName(entry.Name),
            Chance = reused ? RecoveryChance.Poor : RecoveryChance.Good,
            Method = Name,
            Content = new ExtentContent(_source, [new Extent(offset, entry.Size)], entry.Size),
            StartOffset = offset,
        };
    }

    private IEnumerable<FatEntry> ParseDirectory(byte[] data, bool parentDeleted)
    {
        var longName = new List<(string Part, byte Checksum)>();
        for (int p = 0; p + 32 <= data.Length; p += 32)
        {
            var e = data.AsSpan(p, 32);
            byte first = e[0];
            byte attr = e[11];
            if (first == 0x00)
                yield break;

            if (attr == 0x0F)
            {
                longName.Add((ReadLongNamePart(e), e[13]));
                continue;
            }

            if (!LooksLikeShortEntry(e))
            {
                if (parentDeleted)
                    yield break; // ran past the end of a deleted folder into unrelated data
                longName.Clear();
                continue;
            }

            bool deleted = first == 0xE5;
            if ((attr & 0x08) != 0 || first == '.')
            {
                longName.Clear();
                continue;
            }

            var (name, lostFirst) = ResolveName(e, longName, deleted);
            longName.Clear();

            uint cluster = U16(e, 26);
            if (_fatBits == 32)
                cluster |= (uint)U16(e, 20) << 16;

            yield return new FatEntry(
                name,
                cluster,
                U32(e, 28),
                (attr & 0x10) != 0,
                deleted || parentDeleted,
                FatDateTime(U16(e, 24), U16(e, 22)),
                lostFirst);
        }
    }

    private static bool LooksLikeShortEntry(ReadOnlySpan<byte> e)
    {
        if ((e[11] & 0xC0) != 0)
            return false;
        for (int i = 0; i < 11; i++)
        {
            byte c = e[i];
            if (i == 0 && c is 0xE5 or 0x05)
                continue;
            if (c < 0x20 || c is (byte)'"' or (byte)'*' or (byte)'/' or (byte)':' or (byte)'<' or (byte)'>' or (byte)'?' or (byte)'\\' or (byte)'|')
                return false;
        }
        return true;
    }

    /// <summary>
    /// A deleted short name loses its first letter. Camera folders are full of names like IMG_0001.JPG,
    /// so borrow the letter from a sibling with the same pattern (same length and next few characters).
    /// </summary>
    private static IEnumerable<FatEntry> GuessLostFirstLetters(List<FatEntry> entries)
    {
        var known = entries.Where(e => !e.LostFirstLetter).Select(e => e.Name).ToList();
        foreach (var entry in entries)
        {
            if (!entry.LostFirstLetter || entry.Name.Length < 5)
            {
                yield return entry;
                continue;
            }
            string? sibling = known.FirstOrDefault(k => k.Length == entry.Name.Length
                && string.Compare(k, 1, entry.Name, 1, 3, StringComparison.OrdinalIgnoreCase) == 0);
            yield return sibling is null ? entry : entry with { Name = sibling[0] + entry.Name[1..], LostFirstLetter = false };
        }
    }

    private static (string Name, bool LostFirstLetter) ResolveName(ReadOnlySpan<byte> e, List<(string Part, byte Checksum)> longName, bool deleted)
    {
        byte[] shortName = e[..11].ToArray();
        if (shortName[0] == 0x05)
            shortName[0] = 0xE5; // 0x05 is an escaped 0xE5 lead byte

        if (longName.Count > 0 && longName.All(l => l.Checksum == longName[0].Checksum))
        {
            byte checksum = longName[0].Checksum;
            // Long-name entries are stored last-part-first, directly before the short entry.
            string full = string.Concat(Enumerable.Reverse(longName).Select(l => l.Part));
            if (!deleted && ShortNameChecksum(shortName) == checksum && full.Length > 0)
                return (full, false);

            if (deleted && full.Length > 0)
            {
                // The first byte was overwritten; try the long name's first letter, then any character.
                var candidates = new List<byte> { (byte)char.ToUpperInvariant(full[0]) };
                for (byte c = 0x21; c < 0x7F; c++)
                    candidates.Add(c);
                foreach (byte c in candidates)
                {
                    shortName[0] = c;
                    if (ShortNameChecksum(shortName) == checksum)
                        return (full, false);
                }
            }
        }

        string baseName = Encoding.Latin1.GetString(shortName, 0, 8).TrimEnd();
        string ext = Encoding.Latin1.GetString(shortName, 8, 3).TrimEnd();
        if (deleted)
            baseName = "_" + baseName[1..];
        if ((e[12] & 0x08) != 0)
            baseName = baseName.ToLowerInvariant();
        if ((e[12] & 0x10) != 0)
            ext = ext.ToLowerInvariant();
        return (ext.Length > 0 ? $"{baseName}.{ext}" : baseName, deleted);
    }

    private static string ReadLongNamePart(ReadOnlySpan<byte> e)
    {
        var chars = new StringBuilder(13);
        foreach (var (start, count) in new[] { (1, 5), (14, 6), (28, 2) })
        {
            for (int i = 0; i < count; i++)
            {
                char c = (char)U16(e, start + i * 2);
                if (c == '\0' || c == '￿')
                    return chars.ToString();
                chars.Append(c);
            }
        }
        return chars.ToString();
    }

    private static byte ShortNameChecksum(ReadOnlySpan<byte> name)
    {
        byte sum = 0;
        for (int i = 0; i < 11; i++)
            sum = (byte)(((sum & 1) << 7) + (sum >> 1) + name[i]);
        return sum;
    }

    private byte[] ReadChain(uint first)
    {
        var data = new List<byte>();
        uint cluster = first;
        for (int n = 0; n < MaxDirectoryClusters && IsValidCluster(cluster); n++)
        {
            data.AddRange(_source.ReadBytes(ClusterOffset(cluster), (int)_clusterSize));
            uint next = GetFatEntry(cluster);
            if (next == cluster)
                break;
            cluster = next;
            if (cluster != first && !_visited.Add(cluster) && cluster != 0)
                break; // loop in a damaged FAT
        }
        return data.ToArray();
    }

    /// <summary>A deleted folder's chain is gone; read its first cluster, plus following free ones.</summary>
    private byte[] ReadDeletedDirectory(uint first)
    {
        var data = new List<byte>();
        for (uint c = first; c < first + 16 && IsValidCluster(c); c++)
        {
            if (c != first && GetFatEntry(c) != 0)
                break;
            byte[] cluster = _source.ReadBytes(ClusterOffset(c), (int)_clusterSize);
            data.AddRange(cluster);
            if (HasEndMarker(cluster))
                break;
        }
        return data.ToArray();
    }

    private static bool HasEndMarker(byte[] cluster)
    {
        for (int p = 0; p < cluster.Length; p += 32)
            if (cluster[p] == 0)
                return true;
        return false;
    }

    private bool IsValidCluster(uint cluster) => cluster >= 2 && cluster < _clusterCount + 2;

    private long ClusterOffset(uint cluster) => _dataOffset + (cluster - 2) * _clusterSize;

    private uint GetFatEntry(uint cluster)
    {
        switch (_fatBits)
        {
            case 12:
            {
                long o = cluster + cluster / 2;
                if (o + 1 >= _fat.Length)
                    return 0xFFF;
                ushort v = U16(_fat, (int)o);
                uint value = (cluster & 1) != 0 ? (uint)(v >> 4) : (uint)(v & 0x0FFF);
                return value >= 0xFF7 ? 0x0FFFFFFF : value;
            }
            case 16:
            {
                long o = cluster * 2L;
                if (o + 2 > _fat.Length)
                    return 0xFFFF;
                uint value = U16(_fat, (int)o);
                return value >= 0xFFF7 ? 0x0FFFFFFF : value;
            }
            default:
            {
                long o = cluster * 4L;
                if (o + 4 > _fat.Length)
                    return 0x0FFFFFFF;
                return U32(_fat, (int)o) & 0x0FFFFFFF;
            }
        }
    }

    internal static DateTime? FatDateTime(ushort date, ushort time)
    {
        int day = date & 0x1F, month = (date >> 5) & 0x0F, year = 1980 + (date >> 9);
        int second = (time & 0x1F) * 2, minute = (time >> 5) & 0x3F, hour = time >> 11;
        if (day == 0 || month is 0 or > 12 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
            return null;
        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
    }

    internal static string CombinePath(string parent, string name) => parent.EndsWith('\\') ? parent + name : parent + @"\" + name;

    private static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    private static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);

    private sealed record FatEntry(string Name, uint FirstCluster, long Size, bool IsDirectory, bool Deleted, DateTime? Modified, bool LostFirstLetter);
}
