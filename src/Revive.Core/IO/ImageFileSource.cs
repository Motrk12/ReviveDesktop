using Microsoft.Win32.SafeHandles;

namespace Revive.Core.IO;

/// <summary>A disk image file (.img, .dd, .raw, fixed .vhd …) read as if it were a drive.</summary>
public sealed class ImageFileSource : IDiskSource
{
    private readonly SafeFileHandle _handle;

    public ImageFileSource(string path)
    {
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.RandomAccess);
        Length = RandomAccess.GetLength(_handle);
        DisplayName = Path.GetFileName(path);
    }

    public string DisplayName { get; }

    public long Length { get; }

    public int SectorSize => 512;

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= Length)
            return 0;
        if (buffer.Length > Length - offset)
            buffer = buffer[..(int)(Length - offset)];

        int total = 0;
        while (total < buffer.Length)
        {
            int n = RandomAccess.Read(_handle, buffer[total..], offset + total);
            if (n == 0)
                break;
            total += n;
        }
        return total;
    }

    public void Dispose() => _handle.Dispose();
}
