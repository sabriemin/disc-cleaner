namespace DiskTemizleyici.Core.Cleanup;

/// <summary>
/// Last line of defence: refuses to delete places whose loss would break Windows or wipe a user's files
/// by accident, whatever a rule or a click asks for.
/// </summary>
public static class SafetyGuard
{
    private static readonly Lazy<string[]> ProtectedExact = new(() =>
    {
        var list = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), // C:\Users
        };
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            list.Add(Path.Combine(profile, "AppData"));
            list.Add(Path.Combine(profile, "AppData", "LocalLow"));
            list.Add(Path.Combine(profile, "Downloads"));
        }
        return list.Where(p => !string.IsNullOrEmpty(p)).Select(p => Normalize(p!)).Distinct().ToArray();
    });

    /// <summary>Folders below Windows that may be cleaned. Everything else under Windows is off limits.</summary>
    private static readonly Lazy<string[]> AllowedUnderWindows = new(() =>
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new[]
        {
            Path.Combine(win, "Temp"),
            Path.Combine(win, "SoftwareDistribution", "Download"),
        }.Select(Normalize).ToArray();
    });

    /// <summary>True if the path may be deleted (moved to the Recycle Bin or removed).</summary>
    public static bool CanDelete(string path, out string reason)
    {
        reason = "";
        string full;
        try
        {
            full = Normalize(Path.GetFullPath(path));
        }
        catch
        {
            reason = "invalid-path";
            return false;
        }

        if (Path.GetPathRoot(full) is { } root && Normalize(root) == full)
        {
            reason = "drive-root";
            return false;
        }

        if (ProtectedExact.Value.Contains(full))
        {
            reason = "system-folder";
            return false;
        }

        var win = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (IsSameOrUnder(full, win))
        {
            var allowed = AllowedUnderWindows.Value.Any(a => IsUnder(full, a));
            if (!allowed)
            {
                reason = "windows-folder";
                return false;
            }
        }

        return true;
    }

    /// <summary>A cleanup rule's root folder (whose contents get deleted) must not be a protected folder itself.</summary>
    public static bool IsAllowedCleanupRoot(string root)
    {
        var full = Normalize(Path.GetFullPath(root));
        if (Path.GetPathRoot(full) is { } r && Normalize(r) == full) return false;
        if (ProtectedExact.Value.Contains(full)) return false;
        var win = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (IsSameOrUnder(full, win))
            return AllowedUnderWindows.Value.Any(a => IsSameOrUnder(full, a));
        return true;
    }

    internal static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(path).ToUpperInvariant();

    private static bool IsSameOrUnder(string path, string parent) =>
        path == parent || IsUnder(path, parent);

    private static bool IsUnder(string path, string parent) =>
        path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
