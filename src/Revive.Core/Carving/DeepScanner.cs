using Revive.Core.FileSystems;
using Revive.Core.IO;
using Revive.Core.Model;

namespace Revive.Core.Carving;

/// <summary>
/// Searches every sector for the start of a known file type and carves the file out by its structure.
/// Works even when the file system is gone (formatted or damaged drives), but can't recover original
/// names, and files that were stored in scattered pieces may come back damaged.
/// </summary>
public sealed class DeepScanner
{
    private const int ChunkSize = 4 * 1024 * 1024;
    private const int HeaderSize = 512;

    private readonly List<IFileCarver>[] _byLeadByte = new List<IFileCarver>[256];
    private readonly Dictionary<FileCategory, int> _counters = [];

    public DeepScanner(IEnumerable<IFileCarver>? carvers = null)
    {
        foreach (var carver in carvers ?? DefaultCarvers())
            foreach (byte lead in carver.LeadBytes)
                (_byLeadByte[lead] ??= []).Add(carver);
    }

    public static IEnumerable<IFileCarver> DefaultCarvers() =>
    [
        new JpegCarver(), new PngCarver(), new GifCarver(), new BmpCarver(), new TiffCarver(),
        new IsoMediaCarver(), new RiffCarver(), new MatroskaCarver(), new OggCarver(), new Mp3Carver(),
        new PdfCarver(), new ZipCarver(), new OleCarver(), new SevenZipCarver(),
    ];

    /// <param name="skip">Space used by existing files; skipped so results focus on deleted data.</param>
    /// <param name="knownStarts">Start offsets already found by the file-system scan, to avoid duplicates.</param>
    public void Scan(IDiskSource source, IAllocationMap? skip, IReadOnlySet<long>? knownStarts,
        Action<FoundFile> found, IProgress<double>? progress, CancellationToken ct)
    {
        var reader = new SourceReader(source);
        int step = Math.Max(512, source.SectorSize);
        var chunk = new byte[ChunkSize + HeaderSize];
        long length = source.Length;
        long position = 0;

        while (position < length)
        {
            ct.ThrowIfCancellationRequested();
            if (skip is not null)
            {
                long next = skip.NextUnallocated(position);
                if (next >= length)
                    break;
                position = AlignUp(next, step);
            }

            int read = source.Read(position, chunk.AsSpan(0, (int)Math.Min(chunk.Length, length - position)));
            if (read <= 0)
                break;
            int usable = Math.Min(read, ChunkSize);
            long resumeAt = position + usable;

            for (int p = 0; p < usable; p += step)
            {
                long offset = position + p;
                var carvers = _byLeadByte[chunk[p]];
                if (carvers is null || (skip is not null && skip.IsAllocated(offset)))
                    continue;

                var header = chunk.AsSpan(p, Math.Min(HeaderSize, read - p));
                foreach (var carver in carvers)
                {
                    if (!carver.Matches(header))
                        continue;
                    CarveResult? result;
                    try
                    {
                        result = carver.Carve(reader, offset);
                    }
                    catch (Exception ex) when (ex is EndOfStreamException or ArgumentOutOfRangeException or OverflowException or IOException)
                    {
                        result = null;
                    }
                    if (result is null || result.Length <= 0)
                        continue;

                    long length1 = Math.Min(result.Length, length - offset);
                    if (knownStarts is null || !knownStarts.Contains(offset))
                        found(ToFoundFile(source, offset, length1, result));

                    // Continue after this file so pictures embedded inside it aren't reported separately.
                    long end = AlignUp(offset + length1, step);
                    if (end > position + p + step)
                    {
                        if (end >= position + usable)
                        {
                            resumeAt = end;
                            p = usable;
                        }
                        else
                            p = (int)(end - position) - step;
                    }
                    break;
                }
            }

            position = resumeAt;
            progress?.Report((double)Math.Min(position, length) / length);
        }
        progress?.Report(1);
    }

    private FoundFile ToFoundFile(IDiskSource source, long offset, long length, CarveResult result)
    {
        int number;
        lock (_counters)
        {
            _counters.TryGetValue(result.Category, out number);
            _counters[result.Category] = ++number;
        }

        string prefix = result.Category switch
        {
            FileCategory.Photo => "Photo",
            FileCategory.Video => "Video",
            FileCategory.Audio => "Audio",
            FileCategory.Document => "Document",
            FileCategory.Archive => "Archive",
            _ => "File",
        };

        return new FoundFile
        {
            Name = $"{prefix} {number:D5}.{result.Extension}",
            Size = length,
            Modified = result.Taken,
            Category = result.Category,
            Chance = RecoveryChance.Good,
            Method = "Deep scan",
            Content = new ExtentContent(source, [new Extent(offset, length)], length),
            StartOffset = offset,
        };
    }

    private static long AlignUp(long value, int step) => (value + step - 1) / step * step;
}
