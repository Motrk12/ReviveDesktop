using Revive.Core.IO;

namespace Revive.Core.Model;

/// <summary>Where a found file's bytes live and how to copy them out.</summary>
public abstract class FileContent
{
    public abstract Stream OpenRead();

    public virtual async Task SaveToAsync(string destinationPath, IProgress<long>? bytesWritten, CancellationToken ct)
    {
        await using var input = OpenRead();
        await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            bytesWritten?.Report(read);
        }
    }
}

/// <summary>Data stored in pieces on the scanned drive.</summary>
public sealed class ExtentContent(IDiskSource source, IReadOnlyList<Extent> extents, long length) : FileContent
{
    public IReadOnlyList<Extent> Extents { get; } = extents;

    public override Stream OpenRead() => new ExtentStream(source, Extents, length);
}

/// <summary>Small files whose data lives inside the file-system record itself (NTFS resident data).</summary>
public sealed class InlineContent(byte[] data) : FileContent
{
    public override Stream OpenRead() => new MemoryStream(data, writable: false);
}

/// <summary>A file that still exists on disk, e.g. an item in the Recycle Bin.</summary>
public sealed class LocalFileContent(string path) : FileContent
{
    public string Path { get; } = path;

    public override Stream OpenRead() => new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
}

/// <summary>A whole folder in the Recycle Bin.</summary>
public sealed class LocalDirectoryContent(string path) : FileContent
{
    public string Path { get; } = path;

    public override Stream OpenRead() => throw new NotSupportedException("A folder can't be opened as a stream.");

    public override async Task SaveToAsync(string destinationPath, IProgress<long>? bytesWritten, CancellationToken ct)
    {
        Directory.CreateDirectory(destinationPath);
        foreach (var dir in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(System.IO.Path.Combine(destinationPath, System.IO.Path.GetRelativePath(Path, dir)));

        foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            string target = System.IO.Path.Combine(destinationPath, System.IO.Path.GetRelativePath(Path, file));
            await new LocalFileContent(file).SaveToAsync(target, bytesWritten, ct).ConfigureAwait(false);
            File.SetLastWriteTime(target, File.GetLastWriteTime(file));
        }
    }
}
