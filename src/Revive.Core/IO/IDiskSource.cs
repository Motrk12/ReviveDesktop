namespace Revive.Core.IO;

/// <summary>
/// Read-only, random-access view of a drive, partition or disk image.
/// Implementations must be safe to read from several threads at once.
/// </summary>
public interface IDiskSource : IDisposable
{
    string DisplayName { get; }

    long Length { get; }

    int SectorSize { get; }

    /// <summary>Reads up to <paramref name="buffer"/>.Length bytes at <paramref name="offset"/>. Returns 0 at the end.</summary>
    int Read(long offset, Span<byte> buffer);
}

public static class DiskSourceExtensions
{
    /// <summary>Reads exactly buffer.Length bytes or throws <see cref="EndOfStreamException"/>.</summary>
    public static void ReadExactly(this IDiskSource source, long offset, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = source.Read(offset + total, buffer[total..]);
            if (n <= 0)
                throw new EndOfStreamException($"Read past the end of {source.DisplayName} at offset {offset + total}.");
            total += n;
        }
    }

    public static byte[] ReadBytes(this IDiskSource source, long offset, int count)
    {
        var buffer = new byte[count];
        source.ReadExactly(offset, buffer);
        return buffer;
    }
}
