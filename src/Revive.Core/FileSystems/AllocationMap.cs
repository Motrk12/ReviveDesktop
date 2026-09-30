namespace Revive.Core.FileSystems;

/// <summary>Tells the deep scan which parts of the drive belong to existing files so it can skip them.</summary>
public interface IAllocationMap
{
    bool IsAllocated(long offset);

    /// <summary>The first offset at or after <paramref name="offset"/> that is not allocated, or long.MaxValue.</summary>
    long NextUnallocated(long offset);
}

/// <summary>A per-cluster in-use bitmap (bit set = in use) covering a file system's data area.</summary>
public sealed class ClusterAllocationMap : IAllocationMap
{
    private readonly byte[] _bits;
    private readonly long _clusterCount;
    private readonly long _dataStart;
    private readonly long _clusterSize;

    public ClusterAllocationMap(byte[] bits, long clusterCount, long dataStart, long clusterSize)
    {
        _bits = bits;
        _clusterCount = Math.Min(clusterCount, (long)bits.Length * 8);
        _dataStart = dataStart;
        _clusterSize = clusterSize;
    }

    public bool IsClusterAllocated(long index) =>
        index < 0 || index >= _clusterCount || (_bits[index >> 3] & (1 << (int)(index & 7))) != 0;

    public bool IsAllocated(long offset)
    {
        if (offset < _dataStart)
            return true; // boot sector, FATs, root directory
        long index = (offset - _dataStart) / _clusterSize;
        return index < _clusterCount && IsClusterAllocated(index);
    }

    public long NextUnallocated(long offset)
    {
        if (offset < _dataStart)
            offset = _dataStart;
        long index = (offset - _dataStart) / _clusterSize;
        while (index < _clusterCount)
        {
            if ((index & 7) == 0 && _bits[index >> 3] == 0xFF)
            {
                index += 8;
                continue;
            }
            if (!IsClusterAllocated(index))
                return Math.Max(offset, _dataStart + index * _clusterSize);
            index++;
        }
        return Math.Max(offset, _dataStart + _clusterCount * _clusterSize);
    }
}

/// <summary>Combines the maps of several partitions on one disk image.</summary>
public sealed class CompositeAllocationMap(IReadOnlyList<(long Offset, long Length, IAllocationMap Map)> parts) : IAllocationMap
{
    public bool IsAllocated(long offset)
    {
        foreach (var (start, length, map) in parts)
            if (offset >= start && offset < start + length)
                return map.IsAllocated(offset - start);
        return false;
    }

    public long NextUnallocated(long offset)
    {
        foreach (var (start, length, map) in parts)
        {
            if (offset >= start && offset < start + length)
            {
                long next = map.NextUnallocated(offset - start);
                return next >= length ? start + length : start + next;
            }
        }
        return offset;
    }
}
