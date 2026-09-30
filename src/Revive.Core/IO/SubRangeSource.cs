namespace Revive.Core.IO;

/// <summary>A window onto part of another source, e.g. one partition inside a whole-disk image.</summary>
public sealed class SubRangeSource : IDiskSource
{
    private readonly IDiskSource _inner;

    public SubRangeSource(IDiskSource inner, long offset, long length, string displayName)
    {
        _inner = inner;
        Offset = offset;
        Length = Math.Min(length, Math.Max(0, inner.Length - offset));
        DisplayName = displayName;
    }

    public long Offset { get; }

    public string DisplayName { get; }

    public long Length { get; }

    public int SectorSize => _inner.SectorSize;

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= Length)
            return 0;
        if (buffer.Length > Length - offset)
            buffer = buffer[..(int)(Length - offset)];
        return _inner.Read(Offset + offset, buffer);
    }

    /// <summary>The parent owns the underlying handle.</summary>
    public void Dispose() { }
}
