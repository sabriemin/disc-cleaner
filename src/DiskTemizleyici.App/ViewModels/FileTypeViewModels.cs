using CommunityToolkit.Mvvm.ComponentModel;
using DiskTemizleyici.App.Localization;
using DiskTemizleyici.Core;
using DiskTemizleyici.Core.Scanning;

namespace DiskTemizleyici.App.ViewModels;

/// <summary>One category in the "File types" view (videos, images, …) with its biggest extensions.</summary>
public sealed partial class FileCategoryViewModel : ObservableObject
{
    private const int ShownExtensions = 12;

    public FileCategoryViewModel(FileCategory category, IReadOnlyList<FileTypeStat> stats, long total)
    {
        Category = category;
        Bytes = stats.Sum(s => s.Bytes);
        Count = stats.Sum(s => s.Count);
        Share = total > 0 ? 100.0 * Bytes / total : 0;
        Extensions = stats.OrderByDescending(s => s.Bytes).Take(ShownExtensions)
            .Select(s => new FileExtensionViewModel(s, Bytes)).ToList();
    }

    public FileCategory Category { get; }
    public long Bytes { get; }
    public long Count { get; }
    public double Share { get; }
    public IReadOnlyList<FileExtensionViewModel> Extensions { get; }

    [ObservableProperty]
    private bool _isExpanded;

    public string Name => Loc.I["Category_" + Category];
    public string SizeText => ByteFormat.Format(Bytes, Loc.I.Culture);
    public string ShareText => Share.ToString("0.0", Loc.I.Culture) + " %";
    public string CountText => Loc.I.FN("CandidateFiles", Count, Count);

    public string Glyph => Category switch
    {
        FileCategory.Video => "",     // Video
        FileCategory.Image => "",     // Photo
        FileCategory.Audio => "",     // Audio
        FileCategory.Archive => "",   // Package
        FileCategory.DiskImage => "", // HardDrive
        FileCategory.Installer => "", // Download
        FileCategory.GameData => "",  // Game
        FileCategory.Code => "",      // Code
        FileCategory.Document => "",  // Document
        _ => "",                      // Page
    };

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        foreach (var e in Extensions) e.Refresh();
    }

    public override string ToString() => $"{Name}, {SizeText}, {ShareText}";
}

public sealed partial class FileExtensionViewModel : ObservableObject
{
    public FileExtensionViewModel(FileTypeStat stat, long categoryBytes)
    {
        Stat = stat;
        Share = categoryBytes > 0 ? 100.0 * stat.Bytes / categoryBytes : 0;
    }

    public FileTypeStat Stat { get; }
    public double Share { get; }

    public string Name => Stat.Extension.Length == 0 ? Loc.I["NoExtension"] : Stat.Extension;
    public string SizeText => ByteFormat.Format(Stat.Bytes, Loc.I.Culture);
    public string CountText => Loc.I.FN("CandidateFiles", Stat.Count, Stat.Count);

    public void Refresh() => OnPropertyChanged(string.Empty);

    public override string ToString() => $"{Name}, {SizeText}";
}
