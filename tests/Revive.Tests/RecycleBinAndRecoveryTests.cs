using System.Text;
using Revive.Core.Model;
using Revive.Core.RecycleBin;
using Revive.Core.Recovery;

namespace Revive.Tests;

public sealed class RecycleBinAndRecoveryTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("revive-tests-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public void Reads_recycle_bin_items_including_folders()
    {
        string bin = Directory.CreateDirectory(Path.Combine(_temp, "bin")).FullName;
        WriteInfoFile(Path.Combine(bin, "$IABC123.jpg"), @"C:\Users\me\Pictures\Beach day.jpg", 24_698, new DateTime(2026, 9, 1, 10, 30, 0, DateTimeKind.Local));
        File.Copy(TestFiles.MediaPath("photo.jpg"), Path.Combine(bin, "$RABC123.jpg"));

        WriteInfoFile(Path.Combine(bin, "$IDEF456"), @"D:\Work\Old project", 5, new DateTime(2026, 9, 2));
        string folder = Directory.CreateDirectory(Path.Combine(bin, "$RDEF456", "sub")).FullName;
        File.WriteAllText(Path.Combine(folder, "a.txt"), "hello");

        // An info file whose data was already purged is ignored.
        WriteInfoFile(Path.Combine(bin, "$IGONE.txt"), @"C:\gone.txt", 1, DateTime.Now);

        var found = new List<FoundFile>();
        RecycleBinScanner.ScanFolder(bin, found.Add, CancellationToken.None);

        Assert.Equal(2, found.Count);
        var photo = found.Single(f => f.Name == "Beach day.jpg");
        Assert.Equal(@"C:\Users\me\Pictures", photo.FolderPath);
        Assert.Equal(FileCategory.Photo, photo.Category);
        Assert.Equal(new DateTime(2026, 9, 1, 10, 30, 0, DateTimeKind.Local), photo.Deleted);
        Assert.True(found.Single(f => f.Name == "Old project").IsFolder);
    }

    [Fact]
    public async Task Recovers_without_overwriting_and_keeps_folders()
    {
        string bin = Directory.CreateDirectory(Path.Combine(_temp, "bin")).FullName;
        WriteInfoFile(Path.Combine(bin, "$IA.jpg"), @"C:\Users\me\Pictures\Beach day.jpg", 24_698, DateTime.Now);
        File.Copy(TestFiles.MediaPath("photo.jpg"), Path.Combine(bin, "$RA.jpg"));
        WriteInfoFile(Path.Combine(bin, "$IB"), @"D:\Work\Old project", 5, DateTime.Now);
        Directory.CreateDirectory(Path.Combine(bin, "$RB", "sub"));
        File.WriteAllText(Path.Combine(bin, "$RB", "sub", "a.txt"), "hello");

        var files = new List<FoundFile>();
        RecycleBinScanner.ScanFolder(bin, files.Add, CancellationToken.None);
        var carved = new FoundFile
        {
            Name = "Photo 00001.jpg",
            Size = 4,
            Category = FileCategory.Photo,
            Chance = RecoveryChance.Good,
            Method = "Deep scan",
            Content = new InlineContent([1, 2, 3, 4]),
        };
        var odd = new FoundFile
        {
            Name = "what?.txt",
            FolderPath = @"\A:B",
            Size = 1,
            Category = FileCategory.Document,
            Chance = RecoveryChance.Good,
            Method = "FAT32",
            Content = new InlineContent([9]),
        };
        files.AddRange([carved, odd]);

        string destination = Path.Combine(_temp, "out");
        var first = await Recoverer.RecoverAsync(files, destination, keepFolders: true, null, CancellationToken.None);
        var second = await Recoverer.RecoverAsync(files, destination, keepFolders: true, null, CancellationToken.None);

        Assert.Equal(4, first.Recovered);
        Assert.Empty(first.Failures);
        Assert.Equal(4, second.Recovered);
        Assert.True(File.Exists(Path.Combine(destination, "Users", "me", "Pictures", "Beach day.jpg")));
        Assert.True(File.Exists(Path.Combine(destination, "Users", "me", "Pictures", "Beach day (2).jpg")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(destination, "Work", "Old project", "sub", "a.txt")));
        Assert.True(File.Exists(Path.Combine(destination, "Photos", "Photo 00001.jpg")));
        Assert.True(File.Exists(Path.Combine(destination, "A_B", "what_.txt")));
        Assert.Equal(File.ReadAllBytes(TestFiles.MediaPath("photo.jpg")), File.ReadAllBytes(Path.Combine(destination, "Users", "me", "Pictures", "Beach day.jpg")));
    }

    [Theory]
    [InlineData(@"E:\Recovered", 'E', true)]
    [InlineData(@"e:\Recovered", 'E', true)]
    [InlineData(@"D:\Recovered", 'E', false)]
    [InlineData(@"D:\Recovered", null, false)]
    public void Detects_saving_to_the_scanned_drive(string destination, char? source, bool expected) =>
        Assert.Equal(expected, Recoverer.IsSameDrive(destination, source));

    private static void WriteInfoFile(string path, string originalPath, long size, DateTime deleted)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(2L);
        writer.Write(size);
        writer.Write(deleted.ToFileTimeUtc());
        writer.Write(originalPath.Length + 1);
        writer.Write(Encoding.Unicode.GetBytes(originalPath + "\0"));
    }
}
