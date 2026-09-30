using System.Buffers.Binary;
using System.Security.Principal;
using System.Text;
using Revive.Core.Model;

namespace Revive.Core.RecycleBin;

/// <summary>
/// Lists the current user's Recycle Bin on every drive. Each deleted item is a pair of files:
/// $I… holds the original path and deletion time, $R… holds the data.
/// </summary>
public static class RecycleBinScanner
{
    public static void Scan(Action<FoundFile> found, CancellationToken ct)
    {
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null)
            return;

        foreach (var drive in DriveInfo.GetDrives())
        {
            ct.ThrowIfCancellationRequested();
            if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                continue;
            ScanFolder(Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid), found, ct);
        }
    }

    public static void ScanFolder(string folder, Action<FoundFile> found, CancellationToken ct)
    {
        IEnumerable<string> infoFiles;
        try
        {
            if (!Directory.Exists(folder))
                return;
            infoFiles = Directory.EnumerateFiles(folder, "$I*").ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        foreach (string info in infoFiles)
        {
            ct.ThrowIfCancellationRequested();
            var item = TryRead(info);
            if (item is not null)
                found(item);
        }
    }

    internal static FoundFile? TryRead(string infoPath)
    {
        try
        {
            byte[] data = File.ReadAllBytes(infoPath);
            if (data.Length < 24)
                return null;
            long version = BinaryPrimitives.ReadInt64LittleEndian(data);
            long size = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8));
            long deletedAt = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(16));

            string originalPath = version switch
            {
                2 when data.Length >= 28 => Encoding.Unicode.GetString(data, 28,
                    Math.Min(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(24)) * 2, data.Length - 28)),
                1 => Encoding.Unicode.GetString(data, 24, Math.Min(520, data.Length - 24)),
                _ => "",
            };
            originalPath = originalPath.TrimEnd('\0');
            if (originalPath.Length == 0)
                return null;

            string dataPath = Path.Combine(Path.GetDirectoryName(infoPath)!, "$R" + Path.GetFileName(infoPath)[2..]);
            bool isFolder = Directory.Exists(dataPath);
            if (!isFolder && !File.Exists(dataPath))
                return null;

            string name = Path.GetFileName(originalPath.TrimEnd('\\'));
            return new FoundFile
            {
                Name = name,
                FolderPath = Path.GetDirectoryName(originalPath),
                Size = size,
                Modified = isFolder ? Directory.GetLastWriteTime(dataPath) : File.GetLastWriteTime(dataPath),
                Deleted = deletedAt > 0 ? DateTime.FromFileTimeUtc(deletedAt).ToLocalTime() : null,
                Category = isFolder ? FileCategory.Other : FileTypes.FromFileName(name),
                Chance = RecoveryChance.Excellent,
                Method = "Recycle Bin",
                Content = isFolder ? new LocalDirectoryContent(dataPath) : new LocalFileContent(dataPath),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
