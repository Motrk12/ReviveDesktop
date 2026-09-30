using System.Buffers.Binary;

namespace Revive.Core.IO;

/// <summary>
/// Cached random-access reader used by the file-system parsers and carvers.
/// Throws <see cref="EndOfStreamException"/> when asked for data past the end.
/// Not thread-safe; create one per worker.
/// </summary>
public sealed class SourceReader
{
    private const int CacheSize = 64 * 1024;

    private readonly IDiskSource _source;
    private readonly byte[] _cache = new byte[CacheSize];
    private long _cacheStart = -1;
    private int _cacheLength;

    public SourceReader(IDiskSource source) => _source = source;

    public IDiskSource Source => _source;

    public long Length => _source.Length;

    public void Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset + destination.Length > _source.Length)
            throw new EndOfStreamException();

        if (destination.Length > CacheSize / 2)
        {
            _source.ReadExactly(offset, destination);
            return;
        }

        if (_cacheStart < 0 || offset < _cacheStart || offset + destination.Length > _cacheStart + _cacheLength)
            Fill(offset);

        _cache.AsSpan((int)(offset - _cacheStart), destination.Length).CopyTo(destination);
    }

    public byte[] ReadBytes(long offset, int count)
    {
        var bytes = new byte[count];
        Read(offset, bytes);
        return bytes;
    }

    public byte U8(long offset)
    {
        if (_cacheStart >= 0 && offset >= _cacheStart && offset < _cacheStart + _cacheLength)
            return _cache[offset - _cacheStart];
        Span<byte> b = stackalloc byte[1];
        Read(offset, b);
        return b[0];
    }

    public ushort U16LE(long offset) { Span<byte> b = stackalloc byte[2]; Read(offset, b); return BinaryPrimitives.ReadUInt16LittleEndian(b); }
    public uint U32LE(long offset) { Span<byte> b = stackalloc byte[4]; Read(offset, b); return BinaryPrimitives.ReadUInt32LittleEndian(b); }
    public ulong U64LE(long offset) { Span<byte> b = stackalloc byte[8]; Read(offset, b); return BinaryPrimitives.ReadUInt64LittleEndian(b); }
    public ushort U16BE(long offset) { Span<byte> b = stackalloc byte[2]; Read(offset, b); return BinaryPrimitives.ReadUInt16BigEndian(b); }
    public uint U32BE(long offset) { Span<byte> b = stackalloc byte[4]; Read(offset, b); return BinaryPrimitives.ReadUInt32BigEndian(b); }
    public ulong U64BE(long offset) { Span<byte> b = stackalloc byte[8]; Read(offset, b); return BinaryPrimitives.ReadUInt64BigEndian(b); }

    public bool Matches(long offset, ReadOnlySpan<byte> expected)
    {
        if (offset < 0 || offset + expected.Length > _source.Length)
            return false;
        Span<byte> b = expected.Length <= 256 ? stackalloc byte[expected.Length] : new byte[expected.Length];
        Read(offset, b);
        return b.SequenceEqual(expected);
    }

    /// <summary>Finds the next occurrence of <paramref name="pattern"/> in [start, limit). Returns -1 if absent.</summary>
    public long IndexOf(ReadOnlySpan<byte> pattern, long start, long limit)
    {
        limit = Math.Min(limit, _source.Length);
        const int Block = 1 << 20;
        var buffer = new byte[Block + pattern.Length];
        long position = start;
        while (position + pattern.Length <= limit)
        {
            int toRead = (int)Math.Min(buffer.Length, limit - position);
            int read = _source.Read(position, buffer.AsSpan(0, toRead));
            if (read < pattern.Length)
                return -1;
            int index = buffer.AsSpan(0, read).IndexOf(pattern);
            if (index >= 0)
                return position + index;
            position += read - pattern.Length + 1;
        }
        return -1;
    }

    private void Fill(long offset)
    {
        _cacheStart = offset;
        _cacheLength = (int)Math.Min(CacheSize, _source.Length - offset);
        _source.ReadExactly(offset, _cache.AsSpan(0, _cacheLength));
    }
}
