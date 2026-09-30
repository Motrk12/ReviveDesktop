using System.IO;
using Revive.Core.Model;
using Revive.Core.Scanning;

namespace Revive.App.ViewModels;

public enum LocationKind
{
    RecycleBin,
    Drive,
    Image,
}

/// <summary>A place the user can scan: the Recycle Bin, a drive, or a disk image file.</summary>
public sealed class LocationViewModel : ObservableObject
{
    private bool _isSelected;

    private LocationViewModel(LocationKind kind, string title, string subtitle, string glyph)
    {
        Kind = kind;
        Title = title;
        Subtitle = subtitle;
        Glyph = glyph;
    }

    public LocationKind Kind { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Glyph { get; }
    public string? Warning { get; private init; }
    public DriveEntry? Drive { get; private init; }
    public string? ImagePath { get; private init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public bool NeedsAdministrator => Kind == LocationKind.Drive;

    public static LocationViewModel RecycleBin() =>
        new(LocationKind.RecycleBin, "Recycle Bin", "Files you deleted recently from this PC", "");

    public static LocationViewModel ForDrive(DriveEntry drive) =>
        new(LocationKind.Drive, drive.Title,
            drive.IsReady ? $"{drive.FileSystem} · {Formatting.Bytes(drive.TotalSize)}" : "Windows can't read it: try a Deep scan",
            drive.IsRemovable ? "" : "")
        {
            Drive = drive,
            Warning = drive.IsSystem ? "Windows drive: save recovered files to another drive" : null,
        };

    public static LocationViewModel ForImage(string path) =>
        new(LocationKind.Image, Path.GetFileName(path), $"Disk image · {Formatting.Bytes(new FileInfo(path).Length)}", "")
        {
            ImagePath = path,
        };
}

/// <summary>One row in the results list.</summary>
public sealed class FileItemViewModel(FoundFile file, Action<FileItemViewModel, bool> selectionChanged) : ObservableObject
{
    private bool _isSelected;

    public FoundFile File { get; } = file;

    public string Name => File.Name;
    public string Glyph => File.IsFolder ? "" : Formatting.Glyph(File.Category);
    public FileCategory Category => File.Category;
    public long Size => File.Size;
    public string SizeText => Formatting.Bytes(File.Size);
    public DateTime? Date => File.Modified ?? File.Deleted;
    public string DateText => Date?.ToString("g") ?? "";
    public string Folder => File.FolderPath ?? "";
    public string FolderText => File.FolderPath ?? "Found by deep scan (original name unknown)";
    public RecoveryChance Chance => File.Chance;
    public string ChanceText => Formatting.ChanceLabel(File.Chance);
    public string ChanceExplanation => Formatting.ChanceExplanation(File);
    public string TypeText => File.IsFolder ? "Folder" : File.Extension.Length > 0 ? $"{File.Extension.ToUpperInvariant()} file" : "File";
    public string MethodText => File.Method == "Deep scan" ? "Deep scan (by file contents)" : File.Method;
    public string? DeletedText => File.Deleted?.ToString("f");
    public string? ModifiedText => File.Modified?.ToString("f");

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value))
                selectionChanged(this, value);
        }
    }
}

/// <summary>An entry in the category filter on the left.</summary>
public sealed class CategoryViewModel(FileCategory? category, string label, string glyph) : ObservableObject
{
    private int _count;

    public FileCategory? Category { get; } = category;
    public string Label { get; } = label;
    public string Glyph { get; } = glyph;

    public int Count
    {
        get => _count;
        set => Set(ref _count, value);
    }
}
