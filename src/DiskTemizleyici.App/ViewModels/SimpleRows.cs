using DiskTemizleyici.App.Localization;
using DiskTemizleyici.Core;
using DiskTemizleyici.Core.Cleanup;
using DiskTemizleyici.Core.Scanning;

namespace DiskTemizleyici.App.ViewModels;

public sealed class LargeFileViewModel
{
    public LargeFileViewModel(LargeFile file)
    {
        File = file;
        Critical = CriticalFiles.Classify(file.FullPath);
    }

    public LargeFile File { get; }
    public string Name => Path.GetFileName(File.FullPath);
    public string Folder => Path.GetDirectoryName(File.FullPath) ?? "";
    public long Size => File.Size;
    public string SizeText => ByteFormat.Format(File.Size, Loc.I.Culture);
    public string ModifiedText => File.LastWriteTime.ToLocalTime().ToString("d MMM yyyy", Loc.I.Culture);
    public string Extension => Path.GetExtension(File.FullPath).TrimStart('.').ToUpperInvariant();

    /// <summary>".mp4" (lower case, "" without an extension); used by the file-type filter, grouped like the scanner.</summary>
    public string ExtensionKey => FileCategories.ExtensionOf(File.FullPath);

    /// <summary>A whole disk (WSL, Docker, virtual machine) or a Windows system file.</summary>
    public CriticalKind Critical { get; }

    public bool IsCritical => Critical != CriticalKind.None;

    public string CriticalText => IsCritical ? Loc.I["Critical_" + Critical] : "";

    public override string ToString() => IsCritical ? $"{Name}, {SizeText}, {CriticalText}" : $"{Name}, {SizeText}";
}

public sealed class DriveViewModel
{
    private readonly long _total;
    private readonly long _free;

    public DriveViewModel(DriveInfo drive)
    {
        Drive = drive;
        try
        {
            _total = drive.TotalSize;
            _free = drive.TotalFreeSpace;
            Label = drive.VolumeLabel;
        }
        catch (IOException)
        {
            Label = "";
        }
    }

    public DriveInfo Drive { get; }
    public string RootPath => Drive.RootDirectory.FullName;

    /// <summary>"C:"</summary>
    public string Letter => Drive.Name.TrimEnd('\\');

    public string Label { get; }

    public double UsedPercent => _total > 0 ? 100.0 * (_total - _free) / _total : 0;

    public string FreeText => Loc.I.F("DriveFree", ByteFormat.Format(_free, Loc.I.Culture), ByteFormat.Format(_total, Loc.I.Culture));

    public string UsedText => ByteFormat.Format(_total - _free, Loc.I.Culture);

    public long FreeBytes => _free;

    public long UsedBytes => _total - _free;

    public override string ToString() => $"{Letter} {Label}, {FreeText}";
}
