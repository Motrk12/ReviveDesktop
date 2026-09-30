using System.Buffers.Binary;
using System.Text;
using Revive.Core.IO;
using Revive.Core.Model;

namespace Revive.Core.FileSystems;

/// <summary>
/// Finds deleted files on exFAT (most SD cards over 32 GB, many USB drives). Deleting a file clears the
/// "in use" bit of each directory entry but leaves the name, size and first cluster intact.
/// </summary>
public sealed class ExFatVolume : IFileSystemScanner
{
    private const int MaxDirectoryBytes = 64 * 1024 * 1024;

    private readonly IDiskSource _source;
    private readonly long _clusterSize;
    private readonly long _heapOffset;
    private readonly long _clusterCount;
    private readonly uint _rootCluster;
    private readonly byte[] _fat;
    private byte[]? _bitmap;
    private readonly HashSet<uint> _visited = [];

    private ExFatVolume(IDiskSource source, long clusterSize, long heapOffset, long clusterCount, uint rootCluster, byte[] fat)
    {
        _source = source;
        _clusterSize = clusterSize;
        _heapOffset = heapOffset;
        _clusterCount = clusterCount;
        _rootCluster = rootCluster;
        _fat = fat;
    }

    public string Name => "exFAT";

    public static ExFatVolume? TryOpen(IDiskSource source)
    {
        byte[] boot = source.ReadBytes(0, 512);
        if (Encoding.ASCII.GetString(boot, 3, 8) != "EXFAT   ")
            return null;

        int sectorShift = boot[0x6C];
        int clusterShift = boot[0x6D];
        if (sectorShift is < 9 or > 12 || sectorShift + clusterShift > 25)
            return null;

        long sectorSize = 1L << sectorShift;
        long fatOffset = U32(boot, 0x50) * sectorSize;
        long fatLength = U32(boot, 0x54) * sectorSize;
        long heapOffset = U32(boot, 0x58) * sectorSize;
        long clusterCount = U32(boot, 0x5C);
        uint rootCluster = U32(boot, 0x60);
        if (fatLength <= 0 || fatLength > 512L * 1024 * 1024 || clusterCount == 0)
            return null;

        long fatBytes = Math.Min(fatLength, (clusterCount + 2) * 4);
        byte[] fat = source.ReadBytes(fatOffset, (int)fatBytes);
        return new ExFatVolume(source, sectorSize << clusterShift, heapOffset, clusterCount, rootCluster, fat);
    }

    public void FindDeleted(Action<FoundFile> found, IProgress<double>? progress, CancellationToken ct)
    {
        _visited.Clear();
        _visited.Add(_rootCluster);
        var pending = new Stack<(byte[] Data, string Path, bool Deleted)>();
        pending.Push((ReadChain(_rootCluster, MaxDirectoryBytes), @"\", false));

        int processed = 0;
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (data, path, parentDeleted) = pending.Pop();
            foreach (var entry in ParseDirectory(data, parentDeleted))
            {
                if (entry.IsDirectory)
                {
                    if (!IsValidCluster(entry.FirstCluster) || !_visited.Add(entry.FirstCluster))
                        continue;
                    long length = Math.Min(entry.Length, MaxDirectoryBytes);
                    byte[] contents = entry.NoFatChain || entry.Deleted
                        ? ReadContiguous(entry.FirstCluster, length)
                        : ReadChain(entry.FirstCluster, length);
                    pending.Push((contents, FatVolume.CombinePath(path, entry.Name), entry.Deleted));
                }
                else if (entry.Deleted && entry.Length > 0 && IsValidCluster(entry.FirstCluster))
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

    public IAllocationMap? LoadAllocationMap()
    {
        var bitmap = LoadBitmap();
        return bitmap is null ? null : new ClusterAllocationMap(bitmap, _clusterCount, _heapOffset, _clusterSize);
    }

    private FoundFile? ToFoundFile(ExFatEntry entry, string path)
    {
        long clusters = (entry.Length + _clusterSize - 1) / _clusterSize;
        if (entry.FirstCluster + clusters - 1 > _clusterCount + 1)
            return null;

        List<uint> chain;
        RecoveryChance chance;
        if (entry.NoFatChain)
        {
            chain = Enumerable.Range(0, (int)clusters).Select(i => entry.FirstCluster + (uint)i).ToList();
            chance = RecoveryChance.Excellent;
        }
        else
        {
            // Windows often leaves the FAT chain of a deleted file intact; use it if it is complete.
            chain = FollowChain(entry.FirstCluster, clusters);
            if (chain.Count != clusters)
                chain = Enumerable.Range(0, (int)clusters).Select(i => entry.FirstCluster + (uint)i).ToList();
            chance = RecoveryChance.Good;
        }

        var bitmap = LoadBitmap();
        if (bitmap is not null && chain.Any(c => IsSet(bitmap, c - 2)))
            chance = RecoveryChance.Poor;

        var extents = new List<Extent>();
        foreach (uint c in chain)
        {
            long offset = ClusterOffset(c);
            if (extents.Count > 0 && extents[^1].Offset + extents[^1].Length == offset)
                extents[^1] = extents[^1] with { Length = extents[^1].Length + _clusterSize };
            else
                extents.Add(new Extent(offset, _clusterSize));
        }

        return new FoundFile
        {
            Name = entry.Name,
            FolderPath = path,
            Size = entry.Length,
            Modified = entry.Modified,
            Category = FileTypes.FromFileName(entry.Name),
            Chance = chance,
            Method = Name,
            Content = new ExtentContent(_source, extents, entry.Length),
            StartOffset = ClusterOffset(entry.FirstCluster),
        };
    }

    private static IEnumerable<ExFatEntry> ParseDirectory(byte[] data, bool parentDeleted)
    {
        for (int p = 0; p + 32 <= data.Length; p += 32)
        {
            byte type = data[p];
            if (type == 0x00)
                yield break;
            if (type is not (0x85 or 0x05))
                continue;

            int secondaryCount = data[p + 1];
            if (secondaryCount < 2 || secondaryCount > 18 || p + (secondaryCount + 1) * 32 > data.Length)
                continue;

            bool deleted = type == 0x05;
            int stream = p + 32;
            byte streamType = data[stream];
            if (streamType != (deleted ? 0x40 : 0xC0))
                continue;

            int attributes = U16(data, p + 4);
            int nameLength = data[stream + 3];
            var name = new StringBuilder(nameLength);
            for (int s = 2; s <= secondaryCount && name.Length < nameLength; s++)
            {
                int e = p + s * 32;
                if (data[e] != (deleted ? 0x41 : 0xC1))
                    break;
                for (int i = 0; i < 15 && name.Length < nameLength; i++)
                    name.Append((char)U16(data, e + 2 + i * 2));
            }
            if (name.Length == 0)
                continue;

            yield return new ExFatEntry(
                name.ToString(),
                U32(data, stream + 20),
                (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(stream + 24)),
                (data[stream + 1] & 0x02) != 0,
                (attributes & 0x10) != 0,
                deleted || parentDeleted,
                Timestamp(U32(data, p + 12)));

            p += secondaryCount * 32;
        }
    }

    private byte[]? LoadBitmap()
    {
        if (_bitmap is not null)
            return _bitmap;
        byte[] root = ReadChain(_rootCluster, 1024 * 1024);
        for (int p = 0; p + 32 <= root.Length; p += 32)
        {
            if (root[p] == 0x00)
                break;
            if (root[p] != 0x81 || (root[p + 1] & 1) != 0)
                continue; // only the first bitmap (TexFAT has two)
            uint first = U32(root, p + 20);
            long length = (long)BinaryPrimitives.ReadUInt64LittleEndian(root.AsSpan(p + 24));
            if (!IsValidCluster(first) || length <= 0 || length > 256L * 1024 * 1024)
                return null;
            _bitmap = ReadChain(first, length);
            return _bitmap;
        }
        return null;
    }

    private List<uint> FollowChain(uint first, long maxClusters)
    {
        var chain = new List<uint>();
        uint cluster = first;
        while (IsValidCluster(cluster) && chain.Count < maxClusters)
        {
            chain.Add(cluster);
            uint next = GetFatEntry(cluster);
            if (next == 0xFFFFFFFF)
                break;
            if (next <= cluster && chain.Contains(next))
                break;
            cluster = next;
        }
        return chain;
    }

    private byte[] ReadChain(uint first, long maxBytes)
    {
        var chain = FollowChain(first, (maxBytes + _clusterSize - 1) / _clusterSize);
        var data = new byte[Math.Min(maxBytes, chain.Count * _clusterSize)];
        for (int i = 0; i < chain.Count; i++)
        {
            int count = (int)Math.Min(_clusterSize, data.Length - i * _clusterSize);
            if (count <= 0)
                break;
            _source.ReadExactly(ClusterOffset(chain[i]), data.AsSpan((int)(i * _clusterSize), count));
        }
        return data;
    }

    private byte[] ReadContiguous(uint first, long length)
    {
        long clusters = Math.Max(1, (length + _clusterSize - 1) / _clusterSize);
        long available = _clusterCount + 2 - first;
        long bytes = Math.Min(clusters, available) * _clusterSize;
        return _source.ReadBytes(ClusterOffset(first), (int)bytes);
    }

    private bool IsValidCluster(uint cluster) => cluster >= 2 && cluster < _clusterCount + 2;

    private long ClusterOffset(uint cluster) => _heapOffset + (cluster - 2) * _clusterSize;

    private uint GetFatEntry(uint cluster) =>
        cluster * 4L + 4 <= _fat.Length ? U32(_fat, (int)(cluster * 4)) : 0xFFFFFFFF;

    private static bool IsSet(byte[] bits, long index) =>
        index >= 0 && index / 8 < bits.Length && (bits[index / 8] & (1 << (int)(index % 8))) != 0;

    private static DateTime? Timestamp(uint value) =>
        FatVolume.FatDateTime((ushort)(value >> 16), (ushort)(value & 0xFFFF));

    private static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    private static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);

    private sealed record ExFatEntry(string Name, uint FirstCluster, long Length, bool NoFatChain, bool IsDirectory, bool Deleted, DateTime? Modified);
}
