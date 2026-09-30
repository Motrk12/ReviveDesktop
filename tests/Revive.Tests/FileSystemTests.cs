using System.Text;
using Revive.Core.FileSystems;
using Revive.Core.IO;
using Revive.Core.Model;
using Revive.Core.Scanning;

namespace Revive.Tests;

/// <summary>
/// Scans real FAT16/FAT32/exFAT/NTFS images made by the Linux drivers (tests/tools/make-fs-images.sh).
/// Each image holds the same files; some were deleted, including a whole folder.
/// </summary>
public class FileSystemTests
{
    private const string NoteText = "Short note that fits inside the file record.\n";

    [ImageTheory]
    [InlineData("fat32.img", "FAT32")]
    [InlineData("fat16.img", "FAT16")]
    [InlineData("exfat.img", "exFAT")]
    [InlineData("ntfs.img", "NTFS")]
    public void Finds_deleted_files_with_names_folders_and_content(string image, string fileSystem)
    {
        using var source = new ImageFileSource(GeneratedImages.PathOf(image));
        var scanner = FileSystemDetector.Detect(source);
        Assert.NotNull(scanner);
        Assert.Equal(fileSystem, scanner.Name);

        var found = new List<FoundFile>();
        scanner.FindDeleted(found.Add, null, CancellationToken.None);

        AssertRecovered(found, "IMG_0001.JPG", @"\DCIM\100CANON", TestFiles.Media("photo.jpg"));
        AssertRecovered(found, "Holiday video with a long name.mp4", @"\DCIM\100CANON", TestFiles.Media("clip.mp4"));
        AssertRecovered(found, "note.txt", @"\Documents", Encoding.ASCII.GetBytes(NoteText));
        AssertRecovered(found, "song.mp3", @"\Music", TestFiles.Media("song.mp3"));
        AssertRecovered(found, "music.ogg", @"\Music", TestFiles.Media("music.ogg"));

        // On FAT the lost first letter is borrowed from the surviving IMG_0002.JPG next to it.
        Assert.Contains(found, f => f.Name == "IMG_0001.JPG");

        // Files that still exist are not "deleted".
        Assert.DoesNotContain(found, f => f.Name.EndsWith("MG_0002.JPG", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, f => f.Name.Equals("keep.png", StringComparison.OrdinalIgnoreCase));
        Assert.All(found, f => Assert.NotEqual(RecoveryChance.Poor, f.Chance));
    }

    [ImageTheory]
    [InlineData("fat32.img")]
    [InlineData("exfat.img")]
    [InlineData("ntfs.img")]
    public void Deep_scan_skips_existing_files_and_does_not_duplicate_quick_results(string image)
    {
        using var source = new ImageFileSource(GeneratedImages.PathOf(image));
        var found = new List<FoundFile>();
        ScanSession.ScanAsync(source, ScanMode.Deep, f => { lock (found) found.Add(f); }, null, CancellationToken.None).GetAwaiter().GetResult();

        var carved = found.Where(f => f.Method == "Deep scan").ToList();
        var carvedHashes = carved.Select(f => TestFiles.Hash(TestFiles.ReadAll(f))).ToHashSet();

        // The still-existing wallpaper and PNG live in allocated clusters and must be skipped.
        Assert.DoesNotContain(TestFiles.Hash(TestFiles.Media("wallpaper.jpg")), carvedHashes);
        Assert.DoesNotContain(TestFiles.Hash(TestFiles.Media("image.png")), carvedHashes);

        // Deleted files were already found by name, so the deep scan must not list them again.
        Assert.DoesNotContain(TestFiles.Hash(TestFiles.Media("photo.jpg")), carvedHashes);
        Assert.Contains(found, f => f.Method != "Deep scan" && f.Name.EndsWith("MG_0001.JPG", StringComparison.OrdinalIgnoreCase));
    }

    [ImageFact("fat32.img")]
    public void Quick_scan_of_a_formatted_drive_falls_back_to_deep_scan()
    {
        // Wipe the boot sector and the start of the FAT: like a drive Windows says "needs to be formatted".
        byte[] data = File.ReadAllBytes(GeneratedImages.PathOf("fat32.img"));
        Array.Clear(data, 0, 64 * 1024);

        var found = new List<FoundFile>();
        var summary = ScanSession.ScanAsync(new MemorySource(data), ScanMode.Quick, f => { lock (found) found.Add(f); }, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.True(summary.FellBackToDeep);
        var hashes = found.Select(f => TestFiles.Hash(TestFiles.ReadAll(f))).ToHashSet();
        foreach (var media in new[] { "photo.jpg", "wallpaper.jpg", "clip.mp4", "song.mp3", "image.png" })
            Assert.Contains(TestFiles.Hash(TestFiles.Media(media)), hashes);
    }

    [ImageFact("fat32.img")]
    public void Scans_partitions_inside_a_whole_disk_image()
    {
        byte[] volume = File.ReadAllBytes(GeneratedImages.PathOf("fat32.img"));
        const int start = 2048;
        var disk = new byte[start * 512 + volume.Length];
        volume.CopyTo(disk, start * 512);
        // One MBR entry: FAT32 (LBA) partition at sector 2048.
        disk[446 + 4] = 0x0C;
        BitConverter.GetBytes((uint)start).CopyTo(disk, 446 + 8);
        BitConverter.GetBytes((uint)(volume.Length / 512)).CopyTo(disk, 446 + 12);
        disk[510] = 0x55;
        disk[511] = 0xAA;

        var found = new List<FoundFile>();
        var summary = ScanSession.ScanAsync(new MemorySource(disk), ScanMode.Quick, f => { lock (found) found.Add(f); }, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.Equal("FAT32", summary.FileSystem);
        AssertRecovered(found, "IMG_0001.JPG", @"\DCIM\100CANON", TestFiles.Media("photo.jpg"));
    }

    private static void AssertRecovered(List<FoundFile> found, string name, string folder, byte[] expected)
    {
        // FAT overwrites the first letter of a deleted short (8.3) name; accept a guessed first letter.
        var match = found.FirstOrDefault(f => f.Name == name && f.FolderPath == folder)
            ?? found.FirstOrDefault(f => f.Name.Length == name.Length && f.Name[1..].Equals(name[1..], StringComparison.OrdinalIgnoreCase) && f.FolderPath == folder);
        Assert.True(match is not null,
            $"{folder}\\{name} not found. Found: {string.Join(", ", found.Select(f => $"{f.FolderPath}\\{f.Name}"))}");
        Assert.Equal(expected.Length, match.Size);
        Assert.Equal(TestFiles.Hash(expected), TestFiles.Hash(TestFiles.ReadAll(match)));
    }
}

internal static class GeneratedImages
{
    public static string? Folder { get; } = Locate();

    public static string PathOf(string name) => Path.Combine(Folder!, name);

    public static bool Exists(string name) => Folder is not null && File.Exists(PathOf(name));

    private static string? Locate()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "TestData", "generated");
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }
}

/// <summary>Skips when the generated images are missing (they are built by tests/tools/make-fs-images.sh).</summary>
public sealed class ImageFactAttribute : FactAttribute
{
    public ImageFactAttribute(string image)
    {
        if (!GeneratedImages.Exists(image))
            Skip = $"Missing {image}: run tests/tools/make-fs-images.sh";
    }
}

public sealed class ImageTheoryAttribute : TheoryAttribute
{
    public ImageTheoryAttribute()
    {
        if (!GeneratedImages.Exists("fat32.img"))
            Skip = "Missing file-system images: run tests/tools/make-fs-images.sh";
    }
}
