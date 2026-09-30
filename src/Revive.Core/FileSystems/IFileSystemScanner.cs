using Revive.Core.IO;
using Revive.Core.Model;

namespace Revive.Core.FileSystems;

/// <summary>Reads a file system's own records to find deleted files with their names and folders.</summary>
public interface IFileSystemScanner
{
    /// <summary>"NTFS", "FAT32", "exFAT" …</summary>
    string Name { get; }

    void FindDeleted(Action<FoundFile> found, IProgress<double>? progress, CancellationToken ct);

    IAllocationMap? LoadAllocationMap();
}

public static class FileSystemDetector
{
    public static IFileSystemScanner? Detect(IDiskSource source)
    {
        try
        {
            return NtfsVolume.TryOpen(source)
                ?? (IFileSystemScanner?)ExFatVolume.TryOpen(source)
                ?? FatVolume.TryOpen(source);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException)
        {
            return null;
        }
    }
}
