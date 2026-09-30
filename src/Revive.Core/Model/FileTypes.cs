namespace Revive.Core.Model;

public static class FileTypes
{
    private static readonly Dictionary<string, FileCategory> ByExtension = Build(
        (FileCategory.Photo, "jpg jpeg jpe jfif png gif bmp tif tiff webp heic heif avif ico svg psd raw cr2 cr3 crw nef nrw arw srf sr2 dng orf rw2 raf pef srw x3f"),
        (FileCategory.Video, "mp4 m4v mov qt avi wmv mkv webm 3gp 3g2 mts m2ts ts mpg mpeg vob flv f4v ogv asf mxf insv lrv"),
        (FileCategory.Audio, "mp3 m4a m4b aac wav wma flac ogg oga opus amr aiff aif mid midi ape"),
        (FileCategory.Document, "pdf doc docx docm dot dotx xls xlsx xlsm xlt ppt pptx pptm pps ppsx odt ods odp rtf txt csv md epub msg eml xps pages numbers key one pub vsd vsdx html htm xml json"),
        (FileCategory.Archive, "zip rar 7z tar gz tgz bz2 xz cab iso zst"));

    public static FileCategory FromExtension(string extension)
    {
        extension = extension.TrimStart('.').ToLowerInvariant();
        return ByExtension.TryGetValue(extension, out var category) ? category : FileCategory.Other;
    }

    public static FileCategory FromFileName(string name) => FromExtension(Path.GetExtension(name));

    public static string PluralLabel(FileCategory category) => category switch
    {
        FileCategory.Photo => "Photos",
        FileCategory.Video => "Videos",
        FileCategory.Audio => "Audio",
        FileCategory.Document => "Documents",
        FileCategory.Archive => "Archives",
        _ => "Other files",
    };

    private static Dictionary<string, FileCategory> Build(params (FileCategory Category, string Extensions)[] groups)
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var (category, extensions) in groups)
            foreach (var ext in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                map[ext] = category;
        return map;
    }
}
