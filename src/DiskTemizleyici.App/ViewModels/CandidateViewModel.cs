using CommunityToolkit.Mvvm.ComponentModel;
using DiskTemizleyici.App.Localization;
using DiskTemizleyici.Core;
using DiskTemizleyici.Core.Cleanup;

namespace DiskTemizleyici.App.ViewModels;

public sealed partial class CandidateViewModel : ObservableObject
{
    private readonly Action _selectionChanged;

    public CandidateViewModel(CleanupCandidate candidate, Action selectionChanged)
    {
        Candidate = candidate;
        _selectionChanged = selectionChanged;
        _isSelected = candidate.Rule.Risk == RiskLevel.Safe;
    }

    public CleanupCandidate Candidate { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _selectionChanged();

    public string Name => Candidate.Rule.GetName(Loc.I.Language);

    public string Description => Candidate.Rule.GetDescription(Loc.I.Language);

    public string PathsText => string.Join("\n", Candidate.Roots);

    public bool HasPaths => Candidate.Roots.Count > 0;

    public string SizeText => ByteFormat.Format(Candidate.Size, Loc.I.Culture);

    public string FilesText => Loc.I.F("CandidateFiles", Candidate.FileCount);

    public bool IsCaution => Candidate.Rule.Risk == RiskLevel.Caution;

    public string RiskText => Loc.I[IsCaution ? "RiskCaution" : "RiskSafe"];

    public bool NeedsAdmin => Candidate.Rule.NeedsAdmin;

    public string AdminText => Loc.I["NeedsAdmin"];

    /// <summary>Segoe glyph for the card icon.</summary>
    public string Glyph => Candidate.Rule.Id switch
    {
        "recycle-bin" => "",              // Delete
        "browser-cache" => "",            // Globe
        "shader-cache" or "steam-shader-cache" => "", // Game
        "windows-update-download" => "",  // Sync
        "crash-dumps" => "",              // Warning
        "cs2-autosaves" or "cs2-cache" => "",
        "npm-cache" or "pip-cache" or "nuget-http-cache" or "gradle-cache" => "", // Code
        _ => "",                          // Folder
    };

    public void Refresh() => OnPropertyChanged(string.Empty);
}
