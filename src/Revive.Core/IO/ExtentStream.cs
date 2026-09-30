namespace Revive.Core.IO;

/// <summary>A byte range on the source. A negative <see cref="Offset"/> means a sparse run (reads as zeros).</summary>
public readonly record struct Extent(long Offset, long Length)
{
    public bool IsSparse => Offset < 0;
}

/// <summary>Presents a list of extents on a source as one continuous, seekable, read-only stream.</summary>
public sealed class ExtentStream : Stream
{
    private readonly IDiskSource _source;
    private readonly Extent[] _extents;
    private readonly long[] _starts;
    private long _position;

    public ExtentStream(IDiskSource source, IReadOnlyList<Extent> extents, long length)
    {
        _source = source;
        _extents = extents.ToArray();
        _starts = new long[_extents.Length];
        long total = 0;
        for (int i = 0; i < _extents.Length; i++)
        {
            _starts[i] = total;
            total += _extents[i].Length;
        }
        Length = Math.Min(length, total);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, Length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= Length)
            return 0;
        if (buffer.Length > Length - _position)
            buffer = buffer[..(int)(Length - _position)];

        int index = Array.BinarySearch(_starts, _position);
        if (index < 0)
            index = ~index - 1;

        int written = 0;
        while (written < buffer.Length && index < _extents.Length)
        {
            var extent = _extents[index];
            long within = _position - _starts[index];
            int count = (int)Math.Min(buffer.Length - written, extent.Length - within);
            if (count <= 0)
            {
                index++;
                continue;
            }

            var target = buffer.Slice(written, count);
            if (extent.IsSparse)
                target.Clear();
            else
            {
                int read = _source.Read(extent.Offset + within, target);
                if (read < count)
                    target[read..].Clear();
            }

            written += count;
            _position += count;
            if (_position - _starts[index] >= extent.Length)
                index++;
        }
        return written;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => Length + offset,
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
