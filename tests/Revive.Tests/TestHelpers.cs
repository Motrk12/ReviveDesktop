using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Revive.Core.IO;
using Revive.Core.Model;

namespace Revive.Tests;

/// <summary>An in-memory drive.</summary>
internal sealed class MemorySource(byte[] data, string name = "memory") : IDiskSource
{
    public byte[] Data { get; } = data;
    public string DisplayName => name;
    public long Length => Data.Length;
    public int SectorSize => 512;

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset >= Data.Length)
            return 0;
        int count = (int)Math.Min(buffer.Length, Data.Length - offset);
        Data.AsSpan((int)offset, count).CopyTo(buffer);
        return count;
    }

    public void Dispose() { }
}

/// <summary>A write-only stream that can't seek, like a network socket.</summary>
internal sealed class ForwardOnlyStream(Stream inner) : Stream
{
    private long _written;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _written; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        _written += count;
    }
}

internal static class TestFiles
{
    public static string MediaPath(string name) => Path.Combine(AppContext.BaseDirectory, "media", name);

    public static byte[] Media(string name) => File.ReadAllBytes(MediaPath(name));

    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    public static byte[] ReadAll(FoundFile file)
    {
        using var stream = file.Content.OpenRead();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    public static byte[] Noise(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public static byte[] Docx()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Add(zip, "_rels/.rels", "<?xml version=\"1.0\"?><Relationships/>");
            Add(zip, "word/document.xml", "<?xml version=\"1.0\"?><w:document xmlns:w=\"x\"><w:body><w:p><w:r><w:t>" + new string('x', 5000) + "</w:t></w:r></w:p></w:body></w:document>");
        }
        return ms.ToArray();
    }

    public static byte[] Zip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "notes.txt", "hello " + new string('y', 3000));
            // A zip stored inside a zip: its own end record must not end the outer file early.
            var inner = zip.CreateEntry("inner.zip", CompressionLevel.NoCompression);
            using var s = inner.Open();
            s.Write(Docx());
        }
        return ms.ToArray();
    }

    /// <summary>A PDF with an incremental update (two %%EOF markers).</summary>
    public static byte[] Pdf() => Encoding.ASCII.GetBytes(
        "%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\nxref\n0 2\ntrailer\n<< /Root 1 0 R >>\nstartxref\n9\n%%EOF\n" +
        "2 0 obj\n<< /Producer (update) >>\nendobj\nxref\n2 1\ntrailer\n<< /Prev 9 >>\nstartxref\n120\n%%EOF\n");

    private static void Add(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }
}
