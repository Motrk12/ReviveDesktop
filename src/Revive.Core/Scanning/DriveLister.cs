namespace Revive.Core.Scanning;

public sealed record DriveEntry(char Letter, string Label, string FileSystem, long TotalSize, bool IsRemovable, bool IsReady, bool IsSystem)
{
    public string Title => IsReady && Label.Length > 0
        ? $"{Label} ({Letter}:)"
        : IsRemovable ? $"USB drive / memory card ({Letter}:)" : $"Local disk ({Letter}:)";
}

public static class DriveLister
{
    public static IReadOnlyList<DriveEntry> List()
    {
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var drives = new List<DriveEntry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                continue;
            char letter = drive.Name[0];
            bool isSystem = string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase);
            bool removable = drive.DriveType == DriveType.Removable;
            try
            {
                if (drive.IsReady)
                {
                    drives.Add(new DriveEntry(letter, drive.VolumeLabel, drive.DriveFormat, drive.TotalSize, removable, true, isSystem));
                    continue;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            // Not ready: an empty card reader, or a drive Windows can't read (RAW / "needs formatting").
            drives.Add(new DriveEntry(letter, "", "", 0, removable, false, isSystem));
        }
        return drives;
    }
}
