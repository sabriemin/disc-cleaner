using DiskTemizleyici.App.Localization;
using DiskTemizleyici.Core;
using DiskTemizleyici.Core.Scanning;

namespace DiskTemizleyici.App.ViewModels;

public sealed class LargeFileViewModel
{
    public LargeFileViewModel(LargeFile file) => File = file;

    public LargeFile File { get; }
    public string Name => Path.GetFileName(File.FullPath);
    public string Folder => Path.GetDirectoryName(File.FullPath) ?? "";
    public long Size => File.Size;
    public string SizeText => ByteFormat.Format(File.Size, Loc.I.Culture);
    public string ModifiedText => File.LastWriteTime.ToLocalTime().ToString("d MMM yyyy", Loc.I.Culture);
    public string Extension => Path.GetExtension(File.FullPath).TrimStart('.').ToUpperInvariant();
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
}
