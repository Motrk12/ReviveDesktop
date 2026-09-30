using Revive.Core.Model;

namespace Revive.App.ViewModels;

public static class Formatting
{
    public static string Bytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} {units[0]}" : $"{value:0.#} {units[unit]}";
    }

    public static string Duration(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours} h {time.Minutes} min"
        : time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes} min"
        : $"{Math.Max(1, (int)time.TotalSeconds)} s";

    public static string Glyph(FileCategory category) => category switch
    {
        FileCategory.Photo => "",
        FileCategory.Video => "",
        FileCategory.Audio => "",
        FileCategory.Document => "",
        FileCategory.Archive => "",
        _ => "",
    };

    public static string ChanceLabel(RecoveryChance chance) => chance switch
    {
        RecoveryChance.Excellent => "Excellent",
        RecoveryChance.Good => "Good",
        _ => "Poor",
    };

    public static string ChanceExplanation(FoundFile file) => (file.Chance, file.Method) switch
    {
        (_, "Recycle Bin") => "This item is still in the Recycle Bin, so it can be restored exactly as it was.",
        (RecoveryChance.Excellent, _) => "The file's data hasn't been touched since it was deleted. It should open normally.",
        (RecoveryChance.Good, "Deep scan") => "Found by recognising the file's contents. The original name is lost. Large files that were stored in pieces may come back damaged.",
        (RecoveryChance.Good, _) => "The file's data is still free space, so it's most likely intact.",
        _ => "Some of the space this file used has been reused by newer files. It may open partly or not at all.",
    };
}
