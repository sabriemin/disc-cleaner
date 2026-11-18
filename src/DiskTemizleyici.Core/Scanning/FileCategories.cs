namespace DiskTemizleyici.Core.Scanning;

public enum FileCategory
{
    Video,
    Image,
    Audio,
    Archive,
    DiskImage,
    Installer,
    GameData,
    Code,
    Document,
    Other,
}

/// <summary>Total size of all files with one extension (".mp4"; "" for files without an extension).</summary>
public sealed record FileTypeStat(string Extension, FileCategory Category, long Bytes, long Count);

/// <summary>
/// Groups file extensions into rough categories. An extension is only a hint (.dat or .bin can be anything), so the
/// split is approximate.
/// </summary>
public static class FileCategories
{
    private static readonly Dictionary<string, FileCategory> Map = Build();

    public static FileCategory Of(string extension) =>
        Map.TryGetValue(extension ?? "", out var category) ? category : FileCategory.Other;

    /// <summary>
    /// The extension a file is grouped by: ".mp4" in lower case, or "" when it has none. A long suffix such as
    /// "backup.2024-11-18_full" is not a real extension and counts as none.
    /// </summary>
    public static string ExtensionOf(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Length > 12 ? "" : ext.ToLowerInvariant();
    }

    private static Dictionary<string, FileCategory> Build()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory category, params string[] extensions)
        {
            foreach (var e in extensions) map[e] = category;
        }

        Add(FileCategory.Video, ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".flv", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".3gp", ".vob");
        Add(FileCategory.Image, ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif", ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".psd", ".svg", ".ico", ".avif", ".exr", ".tga", ".dds");
        Add(FileCategory.Audio, ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma", ".opus", ".aiff", ".aif", ".mid", ".midi");
        Add(FileCategory.Archive, ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".cab", ".lz", ".lzma");
        Add(FileCategory.DiskImage, ".iso", ".img", ".vhd", ".vhdx", ".avhdx", ".vmdk", ".vdi", ".qcow2", ".wim", ".esd", ".dmg");
        Add(FileCategory.Installer, ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".msp", ".msu", ".nupkg", ".vsix");
        Add(FileCategory.GameData, ".pak", ".vpk", ".bsa", ".ba2", ".forge", ".bundle", ".assets", ".ucas", ".utoc", ".big", ".rpf", ".wad", ".cok", ".bk2", ".sav", ".gcf");
        Add(FileCategory.Code, ".dll", ".pdb", ".obj", ".o", ".lib", ".a", ".so", ".class", ".jar", ".pyc", ".wasm", ".node", ".js", ".mjs", ".cs", ".cpp", ".c", ".h", ".hpp", ".py", ".java", ".go", ".rs", ".ilk", ".ipch", ".pch", ".map");
        Add(FileCategory.Document, ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".odt", ".ods", ".odp", ".epub", ".csv", ".rtf", ".one", ".pst", ".ost");
        return map;
    }
}
