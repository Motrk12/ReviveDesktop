using Revive.Core.Model;

namespace Revive.Core.Recovery;

public sealed record RecoveryProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string CurrentFile);

public sealed record RecoveryFailure(FoundFile File, string Reason);

public sealed record RecoveryReport(int Recovered, IReadOnlyList<RecoveryFailure> Failures, string Destination);

/// <summary>Copies found files into a destination folder, never overwriting anything already there.</summary>
public static class Recoverer
{
    public static async Task<RecoveryReport> RecoverAsync(IReadOnlyList<FoundFile> files, string destination, bool keepFolders,
        IProgress<RecoveryProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        long bytesTotal = files.Sum(f => f.Size);
        long bytesDone = 0;
        int done = 0;
        var failures = new List<RecoveryFailure>();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new RecoveryProgress(done, files.Count, bytesDone, bytesTotal, file.Name));

            string folder = TargetFolder(destination, file, keepFolders);
            string target = "";
            try
            {
                Directory.CreateDirectory(folder);
                target = UniquePath(folder, SanitizeName(file.Name));
                var bytes = new SyncProgress(n =>
                {
                    bytesDone += n;
                    progress?.Report(new RecoveryProgress(done, files.Count, bytesDone, bytesTotal, file.Name));
                });
                await file.Content.SaveToAsync(target, bytes, ct).ConfigureAwait(false);

                if (file.Modified is { } modified && !file.IsFolder)
                    File.SetLastWriteTime(target, modified);
            }
            catch (OperationCanceledException)
            {
                TryDelete(target);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(target);
                failures.Add(new RecoveryFailure(file, ex.Message));
            }
            done++;
        }

        progress?.Report(new RecoveryProgress(done, files.Count, bytesDone, bytesTotal, ""));
        return new RecoveryReport(done - failures.Count, failures, destination);
    }

    /// <summary>True when both paths are on the same drive, where recovering could overwrite lost data.</summary>
    public static bool IsSameDrive(string destination, char? sourceDrive) =>
        sourceDrive is { } letter
        && Path.GetPathRoot(Path.GetFullPath(destination)) is { Length: >= 2 } root
        && char.ToUpperInvariant(root[0]) == char.ToUpperInvariant(letter) && root[1] == ':';

    internal static string TargetFolder(string destination, FoundFile file, bool keepFolders)
    {
        if (file.FolderPath is null)
            return Path.Combine(destination, FileTypes.PluralLabel(file.Category));
        if (!keepFolders)
            return destination;

        // "C:\Users\me\Pictures" or "\DCIM\100CANON" → "Users\me\Pictures" / "DCIM\100CANON"
        string relative = file.FolderPath;
        if (relative.Length >= 2 && relative[1] == ':')
            relative = relative[2..];
        var parts = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries).Select(SanitizeName);
        return Path.Combine([destination, .. parts]);
    }

    internal static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        string clean = new string(chars).Trim().TrimEnd('.');
        return clean.Length == 0 ? "unnamed" : clean;
    }

    internal static string UniquePath(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;
        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            path = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(path) && !Directory.Exists(path))
                return path;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (path.Length > 0 && File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Reports on the calling thread (Progress&lt;T&gt; would post, making byte counts lag).</summary>
    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
