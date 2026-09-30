using System.Buffers.Binary;
using System.Text;
using Revive.Core.IO;

namespace Revive.Core.FileSystems;

public sealed record PartitionInfo(long Offset, long Length, string Description);

/// <summary>Finds partitions in a whole-disk image (MBR or GPT).</summary>
public static class PartitionTable
{
    public static IReadOnlyList<PartitionInfo> Read(IDiskSource source)
    {
        try
        {
            var gpt = ReadGpt(source);
            return gpt.Count > 0 ? gpt : ReadMbr(source);
        }
        catch (EndOfStreamException)
        {
            return [];
        }
    }

    private static List<PartitionInfo> ReadMbr(IDiskSource source)
    {
        var result = new List<PartitionInfo>();
        byte[] mbr = source.ReadBytes(0, 512);
        if (mbr[510] != 0x55 || mbr[511] != 0xAA)
            return result;

        long totalSectors = source.Length / 512;
        for (int i = 0; i < 4; i++)
        {
            var entry = mbr.AsSpan(446 + i * 16, 16);
            byte status = entry[0];
            byte type = entry[4];
            long start = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            long count = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (status is not (0x00 or 0x80) || type == 0 || type == 0xEE || type is 0x05 or 0x0F or 0x85)
                continue;
            if (start == 0 || count == 0 || start >= totalSectors)
                continue;
            result.Add(new PartitionInfo(start * 512, Math.Min(count, totalSectors - start) * 512, $"Partition {i + 1}"));
        }
        return result;
    }

    private static List<PartitionInfo> ReadGpt(IDiskSource source)
    {
        var result = new List<PartitionInfo>();
        if (source.Length < 34 * 512)
            return result;
        byte[] header = source.ReadBytes(512, 512);
        if (Encoding.ASCII.GetString(header, 0, 8) != "EFI PART")
            return result;

        long entriesLba = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(72));
        int count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80)), 256);
        int entrySize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84));
        if (entrySize < 128 || entrySize > 1024)
            return result;

        byte[] entries = source.ReadBytes(entriesLba * 512, count * entrySize);
        for (int i = 0; i < count; i++)
        {
            var entry = entries.AsSpan(i * entrySize, entrySize);
            if (entry[..16].IndexOfAnyExcept((byte)0) < 0)
                continue;
            long first = BinaryPrimitives.ReadInt64LittleEndian(entry[32..]);
            long last = BinaryPrimitives.ReadInt64LittleEndian(entry[40..]);
            if (first <= 0 || last < first)
                continue;
            string name = Encoding.Unicode.GetString(entry.Slice(56, 72)).TrimEnd('\0');
            result.Add(new PartitionInfo(first * 512, (last - first + 1) * 512, string.IsNullOrWhiteSpace(name) ? $"Partition {result.Count + 1}" : name));
        }
        return result;
    }
}
