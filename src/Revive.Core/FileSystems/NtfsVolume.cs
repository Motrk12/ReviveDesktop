using System.Buffers.Binary;
using System.Text;
using Revive.Core.IO;
using Revive.Core.Model;

namespace Revive.Core.FileSystems;

/// <summary>
/// Finds deleted files on NTFS by reading the Master File Table. Deleting a file only clears the
/// record's "in use" flag, so its name, parent folder and cluster runs usually survive.
/// </summary>
public sealed class NtfsVolume : IFileSystemScanner
{
    private const long RootDirectoryRecord = 5;
    private const long BitmapRecord = 6;
    private const int RecordsPerChunk = 1024;

    private readonly IDiskSource _source;
    private readonly long _clusterSize;
    private readonly int _recordSize;
    private readonly long _totalClusters;
    private readonly List<Extent> _mftExtents;
    private readonly long _mftLength;
    private ClusterAllocationMap? _bitmap;
    private bool _bitmapLoaded;

    private NtfsVolume(IDiskSource source, long clusterSize, int recordSize, long totalClusters, List<Extent> mftExtents, long mftLength)
    {
        _source = source;
        _clusterSize = clusterSize;
        _recordSize = recordSize;
        _totalClusters = totalClusters;
        _mftExtents = mftExtents;
        _mftLength = mftLength;
    }

    public string Name => "NTFS";

    public static NtfsVolume? TryOpen(IDiskSource source)
    {
        byte[] boot = source.ReadBytes(0, 512);
        if (Encoding.ASCII.GetString(boot, 3, 8) != "NTFS    ")
            return null;

        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0B));
        int rawSpc = boot[0x0D];
        long sectorsPerCluster = rawSpc <= 0x80 ? rawSpc : 1L << (256 - rawSpc);
        if (bytesPerSector < 256 || bytesPerSector > 4096 || sectorsPerCluster == 0)
            return null;

        long clusterSize = bytesPerSector * sectorsPerCluster;
        long totalSectors = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(0x28));
        long mftCluster = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(0x30));
        sbyte rawRecord = (sbyte)boot[0x40];
        long recordSize = rawRecord > 0 ? rawRecord * clusterSize : 1L << -rawRecord;
        if (recordSize < 256 || recordSize > 65536 || mftCluster <= 0)
            return null;

        long totalClusters = totalSectors * bytesPerSector / clusterSize;
        var volume = new NtfsVolume(source, clusterSize, (int)recordSize, totalClusters, [], 0);

        byte[] record0 = source.ReadBytes(mftCluster * clusterSize, (int)recordSize);
        var mft = volume.ParseRecord(record0, 0, 0);
        if (mft?.Fragments is null || mft.DataSize <= 0)
            return null;

        // A heavily fragmented MFT keeps the rest of its run list in extension records.
        if (mft.AttributeList is not null)
        {
            var partial = volume.ToExtents(MergeRuns(mft.Fragments));
            using var partialStream = new ExtentStream(source, partial, partial.Sum(e => e.Length));
            foreach (long extension in ReadAttributeListDataRecords(mft.AttributeList))
            {
                if ((extension + 1) * recordSize > partialStream.Length)
                    continue;
                var buffer = new byte[recordSize];
                partialStream.Position = extension * recordSize;
                partialStream.ReadExactly(buffer);
                var ext = volume.ParseRecord(buffer, 0, extension);
                if (ext?.Fragments is not null)
                    mft.Fragments.AddRange(ext.Fragments);
            }
        }

        var extents = volume.ToExtents(MergeRuns(mft.Fragments));
        return new NtfsVolume(source, clusterSize, (int)recordSize, totalClusters, extents, mft.DataSize);
    }

    public void FindDeleted(Action<FoundFile> found, IProgress<double>? progress, CancellationToken ct)
    {
        long totalRecords = _mftLength / _recordSize;
        var folders = new Dictionary<long, FolderRecord>();
        var deleted = new List<MftEntry>();
        var extensionFragments = new Dictionary<long, List<DataFragment>>();

        using var mft = new ExtentStream(_source, _mftExtents, _mftLength);
        var chunk = new byte[_recordSize * RecordsPerChunk];

        for (long first = 0; first < totalRecords; first += RecordsPerChunk)
        {
            ct.ThrowIfCancellationRequested();
            int count = (int)Math.Min(RecordsPerChunk, totalRecords - first);
            mft.Position = first * _recordSize;
            int read = mft.ReadAtLeast(chunk.AsSpan(0, count * _recordSize), count * _recordSize, throwOnEndOfStream: false);
            count = read / _recordSize;

            for (int i = 0; i < count; i++)
            {
                long number = first + i;
                MftEntry? entry;
                try
                {
                    entry = ParseRecord(chunk, i * _recordSize, number);
                }
                catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
                {
                    continue; // damaged record
                }
                if (entry is null)
                    continue;

                if (entry.BaseRecord != 0)
                {
                    if (!entry.InUse && entry.Fragments is { Count: > 0 })
                    {
                        if (!extensionFragments.TryGetValue(entry.BaseRecord, out var list))
                            extensionFragments[entry.BaseRecord] = list = [];
                        list.AddRange(entry.Fragments);
                    }
                    continue;
                }

                if (entry.IsDirectory && entry.Name is not null)
                    folders[number] = new FolderRecord(entry.Name, entry.ParentRecord, entry.ParentSequence, entry.Sequence, entry.InUse);
                else if (!entry.InUse && !entry.IsDirectory && entry.Name is not null && number >= 24)
                    deleted.Add(entry);
            }
            progress?.Report(0.9 * (first + count) / totalRecords);
        }

        var bitmap = (ClusterAllocationMap?)LoadAllocationMap();
        foreach (var entry in deleted)
        {
            ct.ThrowIfCancellationRequested();
            var file = ToFoundFile(entry, folders, extensionFragments, bitmap);
            if (file is not null)
                found(file);
        }
        progress?.Report(1);
    }

    public IAllocationMap? LoadAllocationMap()
    {
        if (_bitmapLoaded)
            return _bitmap;
        _bitmapLoaded = true;
        try
        {
            using var mft = new ExtentStream(_source, _mftExtents, _mftLength);
            var record = new byte[_recordSize];
            mft.Position = BitmapRecord * _recordSize;
            mft.ReadExactly(record);
            var entry = ParseRecord(record, 0, BitmapRecord);
            if (entry?.Fragments is null || entry.DataSize <= 0 || entry.DataSize > int.MaxValue)
                return null;

            var bits = new byte[entry.DataSize];
            using var stream = new ExtentStream(_source, ToExtents(MergeRuns(entry.Fragments)), entry.DataSize);
            stream.ReadExactly(bits);
            _bitmap = new ClusterAllocationMap(bits, _totalClusters, 0, _clusterSize);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException)
        {
            _bitmap = null;
        }
        return _bitmap;
    }

    private FoundFile? ToFoundFile(MftEntry entry, Dictionary<long, FolderRecord> folders,
        Dictionary<long, List<DataFragment>> extensionFragments, ClusterAllocationMap? bitmap)
    {
        if (entry.IsCompressedOrEncrypted)
            return null;

        FileContent content;
        RecoveryChance chance;
        long? start = null;

        if (entry.ResidentData is not null)
        {
            if (entry.ResidentData.Length == 0)
                return null;
            content = new InlineContent(entry.ResidentData);
            chance = RecoveryChance.Excellent;
        }
        else
        {
            var fragments = entry.Fragments ?? [];
            if (extensionFragments.TryGetValue(entry.RecordNumber, out var more))
                fragments.AddRange(more);
            if (fragments.Count == 0 || entry.DataSize <= 0)
                return null;

            var runs = MergeRuns(fragments);
            if (runs.Any(r => r.Lcn >= 0 && (r.Lcn + r.Count > _totalClusters || r.Count <= 0)))
                return null; // corrupt run list

            var extents = ToExtents(runs);
            if (extents.Sum(e => e.Length) < entry.DataSize)
                return null; // part of the run list is missing

            content = new ExtentContent(_source, extents, entry.DataSize);
            start = extents.FirstOrDefault(e => !e.IsSparse).Offset;
            chance = bitmap is null ? RecoveryChance.Good
                : runs.Any(r => r.Lcn >= 0 && AnyAllocated(bitmap, r.Lcn, r.Count)) ? RecoveryChance.Poor
                : RecoveryChance.Excellent;
        }

        return new FoundFile
        {
            Name = entry.Name!,
            FolderPath = BuildPath(entry.ParentRecord, entry.ParentSequence, folders),
            Size = entry.ResidentData?.Length ?? entry.DataSize,
            Modified = entry.Modified,
            Category = FileTypes.FromFileName(entry.Name!),
            Chance = chance,
            Method = Name,
            Content = content,
            StartOffset = start,
        };
    }

    private static bool AnyAllocated(ClusterAllocationMap bitmap, long lcn, long count)
    {
        for (long c = lcn; c < lcn + count; c++)
            if (bitmap.IsClusterAllocated(c))
                return true;
        return false;
    }

    private static string BuildPath(long parent, ushort parentSequence, Dictionary<long, FolderRecord> folders)
    {
        var parts = new List<string>();
        long current = parent;
        ushort sequence = parentSequence;
        for (int depth = 0; current != RootDirectoryRecord; depth++)
        {
            if (depth > 255 || !folders.TryGetValue(current, out var folder))
                return parts.Count == 0 ? "(unknown folder)" : @"(unknown folder)\" + string.Join('\\', Enumerable.Reverse(parts));

            // Windows bumps a record's sequence number when it is freed, so a deleted parent is one ahead.
            bool sameFolder = folder.Sequence == sequence || (!folder.InUse && folder.Sequence == (ushort)(sequence + 1));
            if (!sameFolder)
                return parts.Count == 0 ? "(unknown folder)" : @"(unknown folder)\" + string.Join('\\', Enumerable.Reverse(parts));

            parts.Add(folder.Name);
            current = folder.Parent;
            sequence = folder.ParentSequence;
        }
        parts.Reverse();
        return @"\" + string.Join('\\', parts);
    }

    private List<Extent> ToExtents(List<(long Lcn, long Count)> runs) =>
        runs.Select(r => new Extent(r.Lcn < 0 ? -1 : r.Lcn * _clusterSize, r.Count * _clusterSize)).ToList();

    private static List<(long Lcn, long Count)> MergeRuns(List<DataFragment> fragments) =>
        fragments.OrderBy(f => f.StartVcn).SelectMany(f => f.Runs).ToList();

    private MftEntry? ParseRecord(byte[] buffer, int offset, long number)
    {
        var r = buffer.AsSpan(offset, _recordSize);
        if (r[0] != 'F' || r[1] != 'I' || r[2] != 'L' || r[3] != 'E')
            return null;
        if (!ApplyFixups(r))
            return null;

        int attrOffset = U16(r, 0x14);
        int flags = U16(r, 0x16);
        int used = (int)U32(r, 0x18);
        if (attrOffset < 0x30 || attrOffset >= _recordSize)
            return null;
        int limit = used > attrOffset && used <= _recordSize ? used : _recordSize;

        var entry = new MftEntry
        {
            RecordNumber = number,
            Sequence = U16(r, 0x10),
            InUse = (flags & 0x01) != 0,
            IsDirectory = (flags & 0x02) != 0,
            BaseRecord = (long)(U64(r, 0x20) & 0xFFFFFFFFFFFF),
        };

        int nameNamespace = -1;
        for (int a = attrOffset; a + 16 <= limit;)
        {
            uint type = U32(r, a);
            if (type == 0xFFFFFFFF)
                break;
            int length = (int)U32(r, a + 4);
            if (length < 16 || a + length > _recordSize)
                break;

            bool nonResident = r[a + 8] != 0;
            int attrNameLength = r[a + 9];
            var attr = r.Slice(a, length);

            switch (type)
            {
                case 0x10 when !nonResident: // $STANDARD_INFORMATION
                {
                    int co = U16(attr, 0x14);
                    if (U32(attr, 0x10) >= 32 && co + 16 <= length)
                        entry.Modified = FromFileTime(U64(attr, co + 8));
                    break;
                }
                case 0x20: // $ATTRIBUTE_LIST
                    if (!nonResident)
                    {
                        int co = U16(attr, 0x14);
                        int cl = (int)U32(attr, 0x10);
                        if (co + cl <= length)
                            entry.AttributeList = attr.Slice(co, cl).ToArray();
                    }
                    break;
                case 0x30 when !nonResident: // $FILE_NAME
                {
                    int co = U16(attr, 0x14);
                    if (co + 0x42 > length)
                        break;
                    var fn = attr[co..];
                    int nameChars = fn[0x40];
                    int ns = fn[0x41];
                    if (co + 0x42 + nameChars * 2 > length || nameChars == 0)
                        break;
                    // Prefer the long (Win32/POSIX) name over the 8.3 DOS alias.
                    if (entry.Name is null || (nameNamespace == 2 && ns != 2))
                    {
                        ulong parent = U64(fn, 0);
                        entry.ParentRecord = (long)(parent & 0xFFFFFFFFFFFF);
                        entry.ParentSequence = (ushort)(parent >> 48);
                        entry.Name = Encoding.Unicode.GetString(fn.Slice(0x42, nameChars * 2));
                        nameNamespace = ns;
                    }
                    break;
                }
                case 0x80 when attrNameLength == 0: // unnamed $DATA
                {
                    int attrFlags = U16(attr, 0x0C);
                    if ((attrFlags & 0x00FF) != 0 || (attrFlags & 0x4000) != 0)
                        entry.IsCompressedOrEncrypted = true;

                    if (!nonResident)
                    {
                        int co = U16(attr, 0x14);
                        int cl = (int)U32(attr, 0x10);
                        if (co + cl <= length)
                            entry.ResidentData = attr.Slice(co, cl).ToArray();
                    }
                    else
                    {
                        long startVcn = (long)U64(attr, 0x10);
                        int runOffset = U16(attr, 0x20);
                        if (startVcn == 0)
                            entry.DataSize = (long)U64(attr, 0x30);
                        var runs = DecodeRuns(attr, runOffset);
                        if (runs is not null)
                            (entry.Fragments ??= []).Add(new DataFragment(startVcn, runs));
                    }
                    break;
                }
            }
            a += length;
        }
        return entry;
    }

    private bool ApplyFixups(Span<byte> record)
    {
        int usaOffset = U16(record, 4);
        int usaCount = U16(record, 6);
        if (usaCount < 2 || usaOffset + usaCount * 2 > record.Length)
            return false;
        int stride = record.Length / (usaCount - 1);
        if (stride < 256)
            return false;

        ushort usn = U16(record, usaOffset);
        for (int i = 1; i < usaCount; i++)
        {
            int end = i * stride - 2;
            if (U16(record, end) != usn)
                return false;
            record[end] = record[usaOffset + i * 2];
            record[end + 1] = record[usaOffset + i * 2 + 1];
        }
        return true;
    }

    private static List<(long Lcn, long Count)>? DecodeRuns(ReadOnlySpan<byte> attr, int position)
    {
        var runs = new List<(long, long)>();
        long lcn = 0;
        while (position < attr.Length)
        {
            byte header = attr[position++];
            if (header == 0)
                break;
            int lengthSize = header & 0x0F;
            int offsetSize = header >> 4;
            if (lengthSize == 0 || lengthSize > 8 || offsetSize > 8 || position + lengthSize + offsetSize > attr.Length)
                return null;

            long count = 0;
            for (int i = 0; i < lengthSize; i++)
                count |= (long)attr[position + i] << (8 * i);
            position += lengthSize;

            if (offsetSize == 0)
            {
                runs.Add((-1, count)); // sparse
                continue;
            }

            long delta = 0;
            for (int i = 0; i < offsetSize; i++)
                delta |= (long)attr[position + i] << (8 * i);
            if ((attr[position + offsetSize - 1] & 0x80) != 0 && offsetSize < 8)
                delta -= 1L << (8 * offsetSize); // sign-extend
            position += offsetSize;

            lcn += delta;
            runs.Add((lcn, count));
        }
        return runs;
    }

    private static IEnumerable<long> ReadAttributeListDataRecords(byte[] list)
    {
        for (int p = 0; p + 0x1A <= list.Length;)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(p));
            int length = BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(p + 4));
            if (length == 0)
                yield break;
            long startVcn = BinaryPrimitives.ReadInt64LittleEndian(list.AsSpan(p + 8));
            long record = (long)(BinaryPrimitives.ReadUInt64LittleEndian(list.AsSpan(p + 0x10)) & 0xFFFFFFFFFFFF);
            if (type == 0x80 && startVcn > 0)
                yield return record;
            p += length;
        }
    }

    private static DateTime? FromFileTime(ulong value)
    {
        if (value == 0 || value > (ulong)DateTime.MaxValue.ToFileTimeUtc())
            return null;
        return DateTime.FromFileTimeUtc((long)value).ToLocalTime();
    }

    private static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    private static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    private static ulong U64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);

    private sealed record FolderRecord(string Name, long Parent, ushort ParentSequence, ushort Sequence, bool InUse);

    private sealed record DataFragment(long StartVcn, List<(long Lcn, long Count)> Runs);

    private sealed class MftEntry
    {
        public long RecordNumber;
        public ushort Sequence;
        public bool InUse;
        public bool IsDirectory;
        public long BaseRecord;
        public string? Name;
        public long ParentRecord;
        public ushort ParentSequence;
        public DateTime? Modified;
        public byte[]? ResidentData;
        public List<DataFragment>? Fragments;
        public long DataSize;
        public byte[]? AttributeList;
        public bool IsCompressedOrEncrypted;
    }
}
