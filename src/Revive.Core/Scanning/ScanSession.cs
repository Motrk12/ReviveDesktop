using Revive.Core.Carving;
using Revive.Core.FileSystems;
using Revive.Core.IO;
using Revive.Core.Model;
using Revive.Core.RecycleBin;

namespace Revive.Core.Scanning;

public enum ScanMode
{
    /// <summary>File-system records only: fast, keeps names and folders.</summary>
    Quick,

    /// <summary>File-system records, then every free sector for recognisable files.</summary>
    Deep,
}

public sealed record ScanStatus(string Stage, double Fraction);

public sealed record ScanSummary(string? FileSystem, bool FellBackToDeep, long BadSectors, string? Problem = null);

/// <summary>Runs the right scanners for a source and reports everything they find.</summary>
public static class ScanSession
{
    public static Task<ScanSummary> ScanRecycleBinAsync(Action<FoundFile> found, IProgress<ScanStatus>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            progress?.Report(new ScanStatus("Reading the Recycle Bin", 0));
            RecycleBinScanner.Scan(found, ct);
            progress?.Report(new ScanStatus("Done", 1));
            return new ScanSummary(null, false, 0);
        }, ct);

    public static Task<ScanSummary> ScanAsync(IDiskSource source, ScanMode mode, Action<FoundFile> found,
        IProgress<ScanStatus>? progress, CancellationToken ct) =>
        Task.Run(() => Scan(source, mode, found, progress, ct), ct);

    private static ScanSummary Scan(IDiskSource source, ScanMode mode, Action<FoundFile> found, IProgress<ScanStatus>? progress, CancellationToken ct)
    {
        progress?.Report(new ScanStatus("Reading the drive's file system", 0));

        // A volume, or a whole-disk image with a partition table.
        var volumes = new List<(long Offset, IDiskSource Source, IFileSystemScanner Scanner)>();
        var direct = FileSystemDetector.Detect(source);
        if (direct is not null)
            volumes.Add((0, source, direct));
        else
        {
            foreach (var partition in PartitionTable.Read(source))
            {
                var sub = new SubRangeSource(source, partition.Offset, partition.Length, partition.Description);
                if (FileSystemDetector.Detect(sub) is { } scanner)
                    volumes.Add((partition.Offset, sub, scanner));
            }
        }

        bool fellBack = mode == ScanMode.Quick && volumes.Count == 0;
        bool deep = mode == ScanMode.Deep || fellBack;
        double quickShare = deep ? 0.1 : 1.0;
        var knownStarts = new HashSet<long>();
        string? problem = null;

        for (int i = 0; i < volumes.Count; i++)
        {
            var (offset, _, scanner) = volumes[i];
            int index = i;
            var stage = $"Looking for deleted {scanner.Name} file records";
            try
            {
                scanner.FindDeleted(file =>
                {
                    if (file.StartOffset is { } start)
                        knownStarts.Add(offset + start);
                    found(file);
                }, new SyncProgress<double>(f => progress?.Report(new ScanStatus(stage, quickShare * (index + f) / volumes.Count))), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
            {
                // Damaged records shouldn't stop the rest of the scan.
                problem = $"Part of the {scanner.Name} file system is damaged ({ex.Message}).";
                fellBack |= mode == ScanMode.Quick;
                deep = true;
            }
        }

        if (deep)
        {
            IAllocationMap? skip = volumes.Count switch
            {
                0 => null,
                1 when volumes[0].Offset == 0 => volumes[0].Scanner.LoadAllocationMap(),
                _ => new CompositeAllocationMap(volumes
                    .Select(v => (v.Offset, v.Source.Length, v.Scanner.LoadAllocationMap()))
                    .Where(v => v.Item3 is not null)
                    .Select(v => (v.Offset, v.Length, v.Item3!))
                    .ToList()),
            };

            const string Stage = "Searching every free sector for photos, videos and documents";
            new DeepScanner().Scan(source, skip, knownStarts, found,
                new SyncProgress<double>(f => progress?.Report(new ScanStatus(Stage, quickShare + (1 - quickShare) * f))), ct);
        }

        progress?.Report(new ScanStatus("Done", 1));
        long bad = source is RawVolumeSource raw ? raw.BadSectorCount : 0;
        string? fileSystem = volumes.Count > 0 ? string.Join(", ", volumes.Select(v => v.Scanner.Name).Distinct()) : null;
        return new ScanSummary(fileSystem, fellBack, bad, problem);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
