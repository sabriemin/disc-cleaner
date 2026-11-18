namespace DiskTemizleyici.Core.Cleanup;

public enum RuleMode
{
    /// <summary>Delete everything inside the folder, but keep the folder itself.</summary>
    Contents,

    /// <summary>Delete files matching <see cref="CleanupRule.Pattern"/> (recursively), optionally keeping the newest ones.</summary>
    Files,

    /// <summary>Empty the Windows Recycle Bin.</summary>
    RecycleBin,
}

public enum RiskLevel
{
    /// <summary>Only temporary data or caches that programs re-create on their own.</summary>
    Safe,

    /// <summary>Safe for most people, but read the description first (e.g. game autosaves, re-download caches).</summary>
    Caution,
}

/// <summary>
/// A known place that is usually safe to clean. Rules live in <c>Cleanup/rules.json</c>;
/// adding a new one only needs a new JSON entry.
/// </summary>
public sealed class CleanupRule
{
    public required string Id { get; init; }

    public required Dictionary<string, string> Name { get; init; }

    public required Dictionary<string, string> Description { get; init; }

    public RuleMode Mode { get; init; } = RuleMode.Contents;

    public RiskLevel Risk { get; init; } = RiskLevel.Safe;

    /// <summary>
    /// Folders this rule applies to. Environment variables (<c>%LOCALAPPDATA%</c>) are expanded and a
    /// path segment may be <c>*</c> to match every sub-folder (e.g. every browser profile).
    /// </summary>
    public List<string> Paths { get; init; } = new();

    /// <summary>File name pattern for <see cref="RuleMode.Files"/>, e.g. <c>AutoSave*</c>.</summary>
    public string? Pattern { get; init; }

    /// <summary>
    /// For <see cref="RuleMode.Files"/>: keep this many of the newest matches in each folder. Files that share a name
    /// up to the first dot (<c>save1.cok</c> and <c>save1.cok.cid</c>) count as one item.
    /// </summary>
    public int KeepNewest { get; init; }

    /// <summary>Only delete items that were not modified in the last N days.</summary>
    public int MinAgeDays { get; init; }

    /// <summary>Set when cleaning usually needs "Run as administrator".</summary>
    public bool NeedsAdmin { get; init; }

    public string GetName(string lang) =>
        Name.GetValueOrDefault(lang) ?? Name.GetValueOrDefault("en") ?? Id;

    public string GetDescription(string lang) =>
        Description.GetValueOrDefault(lang) ?? Description.GetValueOrDefault("en") ?? "";
}
