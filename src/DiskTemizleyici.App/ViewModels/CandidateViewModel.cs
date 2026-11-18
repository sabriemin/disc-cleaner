using CommunityToolkit.Mvvm.ComponentModel;
using DiskTemizleyici.App.Localization;
using DiskTemizleyici.Core;
using DiskTemizleyici.Core.Cleanup;

namespace DiskTemizleyici.App.ViewModels;

public sealed partial class CandidateViewModel : ObservableObject
{
    private readonly Action _selectionChanged;
    private readonly bool _isAdmin;

    public CandidateViewModel(CleanupCandidate candidate, Action selectionChanged, bool isAdmin)
    {
        Candidate = candidate;
        _selectionChanged = selectionChanged;
        _isAdmin = isAdmin;
        // Risky items and items that cannot be cleaned start switched off.
        _isSelected = candidate.Rule.Risk == RiskLevel.Safe && candidate.IsActionable;
    }

    public CleanupCandidate Candidate { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !IsActionable)
        {
            IsSelected = false;
            return;
        }
        _selectionChanged();
    }

    /// <summary>False for cards that only explain a problem (no permission, could not be checked).</summary>
    public bool IsActionable => Candidate.IsActionable;

    public string Name => Candidate.Rule.GetName(Loc.I.Language);

    public string Description => Candidate.Rule.GetDescription(Loc.I.Language);

    public string PathsText => string.Join("\n", Candidate.Roots);

    public bool HasPaths => Candidate.Roots.Count > 0;

    public string SizeText => ByteFormat.Format(Candidate.Size, Loc.I.Culture);

    public string FilesText => Loc.I.FN("CandidateFiles", Candidate.FileCount, Candidate.FileCount);

    public bool IsCaution => Candidate.Rule.Risk == RiskLevel.Caution;

    public string RiskText => Loc.I[IsCaution ? "RiskCaution" : "RiskSafe"];

    public bool NeedsAdmin => Candidate.Rule.NeedsAdmin;

    public string AdminText => Loc.I["NeedsAdmin"];

    /// <summary>Explains why the card is partly or fully unusable; empty when all is fine.</summary>
    public string StatusNote =>
        Candidate.Error is not null ? Loc.I.F("CandidateError", Candidate.Error)
        // When already elevated, "restart as administrator" cannot help, so say the folders were simply left alone.
        : Candidate.AccessDenied ? Loc.I[_isAdmin ? "CandidateDeniedAdmin" : "CandidateDenied"]
        : "";

    public bool HasStatusNote => StatusNote.Length > 0;

    /// <summary>Offer "Restart as administrator" right on the card when that would help.</summary>
    public bool ShowAdminButton => Candidate.AccessDenied && !_isAdmin;

    /// <summary>Segoe glyph for the card icon.</summary>
    public string Glyph => Candidate.Rule.Id switch
    {
        "recycle-bin" => "",              // Delete
        "browser-cache" => "",            // Globe
        "shader-cache" or "steam-shader-cache" => "", // Game
        "windows-update-download" => "",  // Sync
        "crash-dumps" => "",              // Warning
        "cs2-autosaves" or "cs2-cache" or "steam-web-cache-logs" or "epic-launcher-cache" => "",
        "npm-cache" or "pip-cache" or "nuget-http-cache" or "gradle-cache" or "vscode-cache" => "", // Code
        "teams-cache" or "discord-slack-cache" => "", // Message
        "kernel-crash-dumps" => "",       // Warning
        _ => "",                          // Folder
    };

    public void Refresh() => OnPropertyChanged(string.Empty);
}
