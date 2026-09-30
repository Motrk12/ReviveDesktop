namespace Revive.Core.Model;

public enum FileCategory
{
    Photo,
    Video,
    Audio,
    Document,
    Archive,
    Other,
}

/// <summary>How likely the recovered file is to open correctly.</summary>
public enum RecoveryChance
{
    /// <summary>All of the file's data is still marked as free and its exact location is known.</summary>
    Excellent,

    /// <summary>The data looks intact, but its layout had to be assumed (e.g. a deleted FAT file or a carved file).</summary>
    Good,

    /// <summary>Some of the space the file used has since been reused by other files.</summary>
    Poor,
}

/// <summary>A deleted file that the scan found and can try to recover.</summary>
public sealed class FoundFile
{
    public required string Name { get; init; }

    /// <summary>Original folder, when the file system still remembers it.</summary>
    public string? FolderPath { get; init; }

    public required long Size { get; init; }

    public DateTime? Modified { get; init; }

    public DateTime? Deleted { get; init; }

    public required FileCategory Category { get; init; }

    public required RecoveryChance Chance { get; init; }

    /// <summary>Where the file was found: "Recycle Bin", "NTFS", "FAT32", "exFAT", "Deep scan".</summary>
    public required string Method { get; init; }

    public required FileContent Content { get; init; }

    /// <summary>Byte offset of the first data on the source, used to avoid listing the same file twice.</summary>
    public long? StartOffset { get; init; }

    public bool IsFolder => Content is LocalDirectoryContent;

    public string Extension => Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();
}
