using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Revive.Core.IO;

/// <summary>
/// Reads a mounted volume sector by sector (\\.\E:). Requires administrator rights.
/// The volume is opened read-only and shared, so nothing on it is ever changed.
/// </summary>
public sealed unsafe class RawVolumeSource : IDiskSource
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private const uint IoctlDiskGetDriveGeometry = 0x00070000;
    private const uint FsctlAllowExtendedDasdIo = 0x00090083;
    private const int MaxChunk = 1 << 20;

    private readonly SafeFileHandle _handle;
    private long _badSectors;

    private RawVolumeSource(SafeFileHandle handle, string displayName, long length, int sectorSize)
    {
        _handle = handle;
        DisplayName = displayName;
        Length = length;
        SectorSize = sectorSize;
    }

    public string DisplayName { get; }

    public long Length { get; }

    public int SectorSize { get; }

    /// <summary>Sectors that could not be read (damaged media). They are returned as zeros.</summary>
    public long BadSectorCount => Interlocked.Read(ref _badSectors);

    public static RawVolumeSource Open(char driveLetter)
    {
        string path = $@"\\.\{char.ToUpperInvariant(driveLetter)}:";
        var handle = CreateFileW(path, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error == 5)
                throw new UnauthorizedAccessException("Reading a drive directly needs administrator permission.");
            throw new IOException($"Could not open drive {driveLetter}: ({new Win32Exception(error).Message})");
        }

        // Lets us read the last few sectors, which the file system may not cover.
        DeviceIoControl(handle, FsctlAllowExtendedDasdIo, null, 0, null, 0, out _, IntPtr.Zero);

        long length;
        if (!DeviceIoControl(handle, IoctlDiskGetLengthInfo, null, 0, &length, sizeof(long), out _, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Could not get the size of drive {driveLetter}: ({new Win32Exception(error).Message})");
        }

        int sectorSize = 512;
        DiskGeometry geometry;
        if (DeviceIoControl(handle, IoctlDiskGetDriveGeometry, null, 0, &geometry, sizeof(DiskGeometry), out _, IntPtr.Zero)
            && geometry.BytesPerSector is >= 512 and <= 65536)
        {
            sectorSize = geometry.BytesPerSector;
        }

        return new RawVolumeSource(handle, $"{char.ToUpperInvariant(driveLetter)}:", length, sectorSize);
    }

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= Length || buffer.IsEmpty)
            return 0;
        if (buffer.Length > Length - offset)
            buffer = buffer[..(int)(Length - offset)];

        // Volume handles only accept sector-aligned offsets, lengths and memory.
        long alignedStart = offset / SectorSize * SectorSize;
        long end = offset + buffer.Length;
        int chunk = (int)Math.Min(MaxChunk, AlignUp(end - alignedStart));
        byte* bounce = (byte*)NativeMemory.AlignedAlloc((nuint)chunk, (nuint)Math.Max(SectorSize, 4096));
        try
        {
            int written = 0;
            long position = alignedStart;
            while (position < end)
            {
                int toRead = (int)Math.Min(chunk, AlignUp(end - position));
                ReadAligned(position, bounce, toRead);

                long copyFrom = Math.Max(position, offset);
                int skip = (int)(copyFrom - position);
                int count = (int)Math.Min(toRead - skip, end - copyFrom);
                new ReadOnlySpan<byte>(bounce + skip, count).CopyTo(buffer[written..]);
                written += count;
                position += toRead;
            }
            return written;
        }
        finally
        {
            NativeMemory.AlignedFree(bounce);
        }
    }

    private long AlignUp(long value) => (value + SectorSize - 1) / SectorSize * SectorSize;

    private void ReadAligned(long position, byte* buffer, int count)
    {
        if (TryReadFile(position, buffer, count, out int read) && read == count)
            return;

        // Something in this chunk is unreadable: retry one sector at a time and zero-fill bad ones.
        for (int done = 0; done < count; done += SectorSize)
        {
            if (!TryReadFile(position + done, buffer + done, SectorSize, out read) || read != SectorSize)
            {
                new Span<byte>(buffer + done, SectorSize).Clear();
                Interlocked.Increment(ref _badSectors);
            }
        }
    }

    private bool TryReadFile(long position, byte* buffer, int count, out int read)
    {
        var overlapped = new NativeOverlapped
        {
            OffsetLow = (int)(position & 0xFFFFFFFF),
            OffsetHigh = (int)(position >> 32),
        };
        return ReadFile(_handle, buffer, count, out read, &overlapped);
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct DiskGeometry
    {
        public long Cylinders;
        public int MediaType;
        public int TracksPerCylinder;
        public int SectorsPerTrack;
        public int BytesPerSector;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle handle, byte* buffer, int toRead, out int read, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, void* inBuffer, int inSize, void* outBuffer, int outSize, out int returned, IntPtr overlapped);
}
