using Revive.Core.Carving;
using Revive.Core.Model;

namespace Revive.Tests;

public class CarvingTests
{
    public static TheoryData<string, string, FileCategory> MediaFiles => new()
    {
        { "photo.jpg", "jpg", FileCategory.Photo },
        { "photo_small.jpg", "jpg", FileCategory.Photo },
        { "wallpaper.jpg", "jpg", FileCategory.Photo },
        { "image.png", "png", FileCategory.Photo },
        { "anim.gif", "gif", FileCategory.Photo },
        { "image.bmp", "bmp", FileCategory.Photo },
        { "image.tif", "tif", FileCategory.Photo },
        { "image.webp", "webp", FileCategory.Photo },
        { "clip.mp4", "mp4", FileCategory.Video },
        { "clip.mov", "mov", FileCategory.Video },
        { "clip.avi", "avi", FileCategory.Video },
        { "clip.mkv", "mkv", FileCategory.Video },
        { "clip.webm", "webm", FileCategory.Video },
        { "song.mp3", "mp3", FileCategory.Audio },
        { "voice.wav", "wav", FileCategory.Audio },
        { "music.ogg", "ogg", FileCategory.Audio },
        { "note.opus", "opus", FileCategory.Audio },
        { "track.m4a", "m4a", FileCategory.Audio },
    };

    [Theory]
    [MemberData(nameof(MediaFiles))]
    public void Recovers_each_format_exactly(string file, string extension, FileCategory category)
    {
        byte[] original = TestFiles.Media(file);
        var found = CarveFromDisk(original);

        var match = Assert.Single(found);
        Assert.Equal(extension, match.Extension);
        Assert.Equal(category, match.Category);
        Assert.Equal(TestFiles.Hash(original), TestFiles.Hash(TestFiles.ReadAll(match)));
    }

    [Fact]
    public void Recovers_documents_and_archives()
    {
        foreach (var (data, ext) in new[] { (TestFiles.Docx(), "docx"), (TestFiles.Zip(), "zip"), (TestFiles.Pdf(), "pdf") })
        {
            var match = Assert.Single(CarveFromDisk(data));
            Assert.Equal(ext, match.Extension);
            Assert.Equal(TestFiles.Hash(data), TestFiles.Hash(TestFiles.ReadAll(match)));
        }
    }

    [Fact]
    public void Recovers_zips_written_with_data_descriptors()
    {
        // Zips written to a non-seekable stream record each entry's size after its data.
        var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(new ForwardOnlyStream(ms), System.IO.Compression.ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            writer.Write(new string('z', 20_000));
        }
        byte[] data = ms.ToArray();
        Assert.True((BitConverter.ToUInt16(data, 6) & 0x08) != 0, "expected a data-descriptor zip");

        var match = Assert.Single(CarveFromDisk(data));
        Assert.Equal("docx", match.Extension);
        Assert.Equal(TestFiles.Hash(data), TestFiles.Hash(TestFiles.ReadAll(match)));
    }

    [Fact]
    public void Rejects_a_damaged_zip()
    {
        byte[] docx = TestFiles.Docx();
        Assert.True(docx.Length > 300);
        byte[] damaged = docx.Take(100).Concat(TestFiles.Noise(docx.Length, 7)).ToArray();
        Assert.Empty(CarveFromDisk(damaged));
    }

    [Fact]
    public void Finds_every_file_on_a_mixed_disk_without_false_positives()
    {
        var originals = MediaFiles.Select(row => TestFiles.Media((string)row[0])).ToList();
        originals.Add(TestFiles.Docx());
        originals.Add(TestFiles.Pdf());

        var disk = new List<byte>();
        int seed = 1;
        foreach (var data in originals)
        {
            disk.AddRange(TestFiles.Noise(512 * 7, seed++));
            disk.AddRange(data);
            disk.AddRange(new byte[(512 - data.Length % 512) % 512]);
        }
        disk.AddRange(TestFiles.Noise(512 * 64, seed));

        var found = new List<FoundFile>();
        new DeepScanner().Scan(new MemorySource(disk.ToArray()), null, null, found.Add, null, CancellationToken.None);

        var expected = originals.Select(TestFiles.Hash).OrderBy(h => h).ToList();
        var actual = found.Select(f => TestFiles.Hash(TestFiles.ReadAll(f))).OrderBy(h => h).ToList();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Truncated_files_at_end_of_disk_are_not_reported()
    {
        byte[] jpeg = TestFiles.Media("photo.jpg");
        var disk = TestFiles.Noise(512 * 4, 3).Concat(jpeg.Take(jpeg.Length / 2)).ToArray();
        disk = disk.Concat(new byte[(512 - disk.Length % 512) % 512]).ToArray();

        var found = new List<FoundFile>();
        new DeepScanner().Scan(new MemorySource(disk), null, null, found.Add, null, CancellationToken.None);
        Assert.Empty(found);
    }

    private static List<FoundFile> CarveFromDisk(byte[] file)
    {
        var disk = new List<byte>();
        disk.AddRange(TestFiles.Noise(512 * 5, 42));
        disk.AddRange(file);
        disk.AddRange(new byte[(512 - file.Length % 512) % 512]);
        disk.AddRange(TestFiles.Noise(512 * 5, 43));

        var found = new List<FoundFile>();
        new DeepScanner().Scan(new MemorySource(disk.ToArray()), null, null, found.Add, null, CancellationToken.None);
        return found;
    }
}
