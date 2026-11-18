using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DiskTemizleyici.Core.Cleanup;

/// <summary>Why a path may not be deleted.</summary>
public enum BlockReason
{
    None,
    InvalidPath,
    NetworkPath,
    DriveRoot,
    SystemFolder,
    WindowsFolder,
    UserFolder,
    SyncedFolder,

    /// <summary>pagefile.sys, hiberfil.sys, swapfile.sys: Windows shrinks these with its own settings.</summary>
    SystemFile,

    /// <summary>Windows.old: removed through Settings, which keeps "Go back" consistent.</summary>
    PreviousWindows,

    /// <summary>A rule's item now lies behind a junction or symbolic link, or outside the rule's folder.</summary>
    RedirectedPath,
}

/// <summary>
/// Last line of defence: refuses to delete places whose loss would break Windows, wipe someone's files or
/// delete data in the cloud, whatever a rule or a click asks for.
/// <para>
/// Paths are compared in every form that can reach the same folder: as written, with 8.3 short names expanded,
/// and with junctions, symbolic links and SUBST drives in the parent folders resolved. So
/// <c>\\?\C:\Windows</c>, <c>C:\PROGRA~1</c> or a junction pointing into Windows are all caught.
/// </para>
/// </summary>
public static class SafetyGuard
{
    private sealed class Protections
    {
        public required Dictionary<string, BlockReason> Exact { get; init; }
        public required List<string> SystemSubtrees { get; init; }
        public required List<string> WindowsFolders { get; init; }
        public required List<string> AllowedUnderWindows { get; init; }
        public required List<string> SyncRoots { get; init; }
        public required List<string> WarnSubtrees { get; init; }
        public required List<string> PreviousWindows { get; init; }
    }

    /// <summary>Files Windows keeps at the root of a drive; never deleted by hand.</summary>
    private static readonly HashSet<string> SystemFilesAtRoot = new(StringComparer.Ordinal)
    {
        "PAGEFILE.SYS", "HIBERFIL.SYS", "SWAPFILE.SYS", "DUMPSTACK.LOG.TMP",
    };

    private static readonly object Gate = new();
    private static Protections? _protections;
    private static List<string>? _syncRootOverride;

    private static Protections P
    {
        get
        {
            lock (Gate) return _protections ??= Build();
        }
    }

    /// <summary>Folders synced to the cloud (OneDrive and other sync providers). Nothing inside them is deleted.</summary>
    public static IReadOnlyList<string> SyncRoots => P.SyncRoots;

    /// <summary>Tests only: replace the detected sync roots (null = detect again).</summary>
    internal static void OverrideSyncRootsForTests(IEnumerable<string>? roots)
    {
        lock (Gate)
        {
            _syncRootOverride = roots?.ToList();
            _protections = null;
        }
    }

    /// <summary>True if the path may be deleted (moved to the Recycle Bin or removed).</summary>
    public static bool CanDelete(string path, out BlockReason reason) => new PathChecker().CanDelete(path, out reason);

    /// <summary>
    /// True for places that are allowed but deserve a stronger warning, such as the Microsoft folders inside
    /// AppData and ProgramData, or Windows.old.
    /// </summary>
    public static bool NeedsExtraWarning(string path)
    {
        var forms = new PathChecker().Forms(path, out _);
        return forms.Any(f => P.WarnSubtrees.Any(w => IsSameOrUnder(f, w)));
    }

    /// <summary>
    /// A cleanup rule's root folder (whose contents get deleted) must not be a protected folder itself, and must not
    /// be inside a protected area, except the Windows folders that may be cleaned (Temp, update downloads, crash dumps).
    /// </summary>
    public static bool IsAllowedCleanupRoot(string root)
    {
        var checker = new PathChecker();
        var forms = checker.Forms(root, out var invalid);
        if (invalid != BlockReason.None || forms.Count == 0) return false;

        // The root's contents get deleted, so the root itself is resolved too: a cache folder that is a junction to
        // D:\ (or to someone's Documents) is judged by where it really points.
        if (Directory.Exists(root) && FinalPath(root) is { } final)
        {
            if (final.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            forms.Add(Normalize(final));
        }

        foreach (var form in forms.Distinct(StringComparer.Ordinal))
        {
            if (P.AllowedUnderWindows.Any(a => IsSameOrUnder(form, a))) continue;
            if (Check(form) != BlockReason.None) return false;
        }
        return true;
    }

    /// <summary>
    /// Checks many paths with shared caches: one per delete operation. Resolving a folder's final path needs a
    /// file handle, so the result is remembered for every parent folder.
    /// </summary>
    public sealed class PathChecker
    {
        private readonly Dictionary<string, string?> _finalFolders = new(StringComparer.OrdinalIgnoreCase);

        public bool CanDelete(string path, out BlockReason reason)
        {
            var forms = Forms(path, out reason);
            if (reason != BlockReason.None) return false;
            foreach (var form in forms)
            {
                reason = Check(form);
                if (reason != BlockReason.None) return false;
            }

            // Hidden + system junctions ("Documents and Settings", "Application Data", "My Documents") exist for old
            // programs; Windows created them and they stay.
            try
            {
                var attrs = File.GetAttributes(path);
                const FileAttributes compat = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System;
                if ((attrs & compat) == compat && (attrs & FileAttributes.Directory) != 0)
                {
                    reason = BlockReason.SystemFolder;
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }
            return true;
        }

        /// <summary>All normalised forms of a path (upper case, no trailing separator, no \\?\ prefix).</summary>
        internal List<string> Forms(string path, out BlockReason invalid)
        {
            invalid = BlockReason.None;
            var forms = new List<string>();
            if (string.IsNullOrWhiteSpace(path))
            {
                invalid = BlockReason.InvalidPath;
                return forms;
            }

            var p = path.Trim().Replace('/', '\\');
            if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                invalid = BlockReason.NetworkPath;
                return forms;
            }
            if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal)) p = p[4..];
            if (p.StartsWith(@"\\", StringComparison.Ordinal))
            {
                invalid = BlockReason.NetworkPath; // \\server\share, \\localhost\C$ ...
                return forms;
            }
            // Only "X:\..." from here on. Anything else (Volume{GUID}\..., relative paths) would be resolved against
            // the current folder by GetFullPath and slip past every check.
            if (p.Length < 3 || !char.IsAsciiLetter(p[0]) || p[1] != ':' || p[2] != '\\')
            {
                invalid = BlockReason.InvalidPath;
                return forms;
            }

            string full;
            try
            {
                full = Path.GetFullPath(p);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                invalid = BlockReason.InvalidPath;
                return forms;
            }
            if (!Path.IsPathFullyQualified(full) || full.Length < 3 || full[1] != ':')
            {
                invalid = BlockReason.InvalidPath;
                return forms;
            }

            forms.Add(Normalize(full));

            var longName = LongPath(full);
            if (longName is not null) forms.Add(Normalize(longName));

            var resolved = ResolveParents(longName ?? full);
            if (resolved is not null)
            {
                if (resolved.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    invalid = BlockReason.NetworkPath;
                    return forms;
                }
                forms.Add(Normalize(resolved));
            }
            return forms.Distinct(StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// The path with junctions, symbolic links and SUBST drives in its parent folders resolved. The item itself is
        /// not resolved: deleting a link removes only the link.
        /// </summary>
        private string? ResolveParents(string path)
        {
            var parent = Path.GetDirectoryName(path);
            if (parent is null) return FinalPath(path); // a drive root: resolve it (SUBST drives)

            var finalParent = FinalFolder(parent);
            return finalParent is null ? null : Path.Join(finalParent, Path.GetFileName(path));
        }

        private string? FinalFolder(string folder)
        {
            if (_finalFolders.TryGetValue(folder, out var cached)) return cached;

            string? result;
            if (Directory.Exists(folder))
            {
                result = FinalPath(folder);
            }
            else
            {
                // walk up to the nearest folder that exists and put the rest back on
                var up = Path.GetDirectoryName(folder);
                var upFinal = up is null ? null : FinalFolder(up);
                result = upFinal is null ? null : Path.Join(upFinal, Path.GetFileName(folder));
            }
            _finalFolders[folder] = result;
            return result;
        }
    }

    // ------------------------------------------------------------------------------------------------ checks

    private static BlockReason Check(string form)
    {
        var p = P;
        var root = Path.GetPathRoot(form);
        if (root is not null && Normalize(root) == form) return BlockReason.DriveRoot;
        if (root is not null && Path.GetDirectoryName(form) is { } parent && Normalize(parent) == Normalize(root)
            && SystemFilesAtRoot.Contains(Path.GetFileName(form)))
            return BlockReason.SystemFile;
        if (p.Exact.TryGetValue(form, out var exact)) return exact;
        if (p.PreviousWindows.Any(w => IsSameOrUnder(form, w))) return BlockReason.PreviousWindows;
        if (p.SyncRoots.Any(s => IsSameOrUnder(form, s))) return BlockReason.SyncedFolder;
        foreach (var win in p.WindowsFolders)
        {
            if (!IsSameOrUnder(form, win)) continue;
            if (!p.AllowedUnderWindows.Any(a => IsUnder(form, a))) return BlockReason.WindowsFolder;
        }
        if (p.SystemSubtrees.Any(s => IsSameOrUnder(form, s))) return BlockReason.SystemFolder;
        return BlockReason.None;
    }

    private static Protections Build()
    {
        var exact = new Dictionary<string, BlockReason>(StringComparer.Ordinal);
        void Add(string? path, BlockReason reason)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var n = Normalize(Path.GetFullPath(path));
                exact.TryAdd(n, reason);
                var longName = LongPath(path);
                if (longName is not null) exact.TryAdd(Normalize(longName), reason);
                // where the folder really is, if it was moved with a junction (C:\Users -> D:\Users)
                if (Directory.Exists(path) && FinalPath(path) is { } real && !real.StartsWith(@"\\", StringComparison.Ordinal))
                    exact.TryAdd(Normalize(real), reason);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var f in new[]
                 {
                     Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
                     Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
                     Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.CommonDesktopDirectory,
                     Environment.SpecialFolder.CommonDocuments, Environment.SpecialFolder.CommonStartMenu,
                 })
        {
            Add(Environment.GetFolderPath(f), BlockReason.SystemFolder);
        }
        Add(Environment.GetEnvironmentVariable("ProgramW6432"), BlockReason.SystemFolder);

        // The current user's known folders, wherever they were moved to (D:\Docs, OneDrive\Documents, ...).
        foreach (var f in new[]
                 {
                     Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.Desktop,
                     Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
                     Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
                     Environment.SpecialFolder.Favorites, Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.Programs,
                 })
        {
            Add(Environment.GetFolderPath(f), BlockReason.UserFolder);
        }

        // Every profile on this PC, not only the current one: other accounts, Public, Default.
        var profiles = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        string? usersFolder = Path.GetDirectoryName(profiles[0]);
        try
        {
            using var list = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (list is not null)
            {
                foreach (var name in new[] { "ProfilesDirectory", "Public", "Default" })
                {
                    if (list.GetValue(name) is string v)
                    {
                        var expanded = Environment.ExpandEnvironmentVariables(v);
                        if (name == "ProfilesDirectory") usersFolder = expanded;
                        else profiles.Add(expanded);
                    }
                }
                foreach (var sid in list.GetSubKeyNames())
                {
                    using var key = list.OpenSubKey(sid);
                    if (key?.GetValue("ProfileImagePath") is string image) profiles.Add(Environment.ExpandEnvironmentVariables(image));
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        if (usersFolder is not null)
        {
            Add(usersFolder, BlockReason.SystemFolder);
            profiles.Add(Path.Combine(usersFolder, "Public"));
            profiles.Add(Path.Combine(usersFolder, "Default"));
        }

        string[] profileFolders =
        {
            "AppData", @"AppData\Local", @"AppData\LocalLow", @"AppData\Roaming", "Desktop", "Documents", "Downloads",
            "Pictures", "Music", "Videos", "Favorites", "Saved Games", "Contacts", "Links", "Searches", "3D Objects", "OneDrive",
        };
        var warn = new List<string>();
        var subtrees = new List<string>();
        foreach (var profile in profiles.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Add(profile, BlockReason.UserFolder);
            foreach (var sub in profileFolders) Add(Path.Combine(profile, sub), BlockReason.UserFolder);
            warn.Add(Normalize(Path.Combine(profile, @"AppData\Local\Microsoft")));
            warn.Add(Normalize(Path.Combine(profile, @"AppData\Roaming\Microsoft")));
            // installers keep their packages here to repair and uninstall programs later
            subtrees.Add(Normalize(Path.Combine(profile, @"AppData\Local\Package Cache")));
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(programData))
        {
            warn.Add(Normalize(Path.Combine(programData, "Microsoft")));
            subtrees.Add(Normalize(Path.Combine(programData, "Package Cache")));
        }

        // Whole areas that are never touched.
        foreach (var drive in DriveInfo.GetDrives())
        {
            var r = drive.RootDirectory.FullName;
            subtrees.Add(Normalize(Path.Combine(r, "System Volume Information")));
            subtrees.Add(Normalize(Path.Combine(r, "$Recycle.Bin"))); // emptied only through the Recycle Bin itself
        }
        var previousWindows = new List<string>();
        var systemRoot = Path.GetPathRoot(windows);
        if (!string.IsNullOrEmpty(systemRoot))
        {
            foreach (var name in new[] { "Recovery", "Boot", "EFI", "$WinREAgent", "$SysReset", "$Windows.~BT", "$Windows.~WS", "Config.Msi" })
                subtrees.Add(Normalize(Path.Combine(systemRoot, name)));
            previousWindows.Add(Normalize(Path.Combine(systemRoot, "Windows.old")));
        }

        var windowsFolders = new List<string>();
        if (!string.IsNullOrEmpty(windows))
        {
            windowsFolders.Add(Normalize(windows));
            var longWindows = LongPath(windows);
            if (longWindows is not null) windowsFolders.Add(Normalize(longWindows));
        }
        var allowedUnderWindows = string.IsNullOrEmpty(windows)
            ? new List<string>()
            : new List<string>
            {
                Normalize(Path.Combine(windows, "Temp")),
                Normalize(Path.Combine(windows, "SoftwareDistribution", "Download")),
                Normalize(Path.Combine(windows, "Minidump")),          // blue-screen minidumps
                Normalize(Path.Combine(windows, "LiveKernelReports")), // live kernel dumps
            };

        return new Protections
        {
            Exact = exact,
            SystemSubtrees = AndRealLocations(subtrees),
            WindowsFolders = AndRealLocations(windowsFolders),
            AllowedUnderWindows = allowedUnderWindows,
            SyncRoots = AndRealLocations((_syncRootOverride ?? DetectSyncRoots()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(Normalize)),
            WarnSubtrees = AndRealLocations(warn),
            PreviousWindows = AndRealLocations(previousWindows),
        };
    }

    /// <summary>The given (normalised) folders plus, for those moved with a junction, where they really are.</summary>
    private static List<string> AndRealLocations(IEnumerable<string> folders)
    {
        var result = new List<string>();
        foreach (var folder in folders)
        {
            result.Add(folder);
            if (Directory.Exists(folder) && FinalPath(folder) is { } real && !real.StartsWith(@"\\", StringComparison.Ordinal))
                result.Add(Normalize(real));
        }
        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>OneDrive folders and every other registered cloud sync root.</summary>
    private static List<string> DetectSyncRoots()
    {
        var roots = new List<string>();
        foreach (var v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var value = Environment.GetEnvironmentVariable(v);
            if (!string.IsNullOrWhiteSpace(value)) roots.Add(value);
        }
        try
        {
            using var manager = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
            foreach (var provider in manager?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var users = manager!.OpenSubKey(provider + @"\UserSyncRoots");
                foreach (var name in users?.GetValueNames() ?? Array.Empty<string>())
                {
                    if (users!.GetValue(name) is string path) roots.Add(path);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        try
        {
            using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            foreach (var account in accounts?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var key = accounts!.OpenSubKey(account);
                if (key?.GetValue("UserFolder") is string folder) roots.Add(folder);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        var result = new List<string>();
        foreach (var r in roots)
        {
            try
            {
                var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(r).Replace(@"\\?\", ""));
                if (Path.GetPathRoot(full) is { } root && Normalize(root) == Normalize(full)) continue; // never treat a whole drive as synced
                result.Add(full);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }
        return result;
    }

    // ------------------------------------------------------------------------------------------------ helpers

    internal static string Normalize(string path)
    {
        var p = path.Replace('/', '\\');
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) && !p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = p[4..];
        return Path.TrimEndingDirectorySeparator(p).ToUpperInvariant();
    }

    private static bool IsSameOrUnder(string path, string parent) => path == parent || IsUnder(path, parent);

    private static bool IsUnder(string path, string parent) =>
        path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>8.3 short names expanded (C:\PROGRA~1 → C:\Program Files); null if the path does not exist.</summary>
    private static string? LongPath(string path)
    {
        var buffer = new StringBuilder(512);
        var n = GetLongPathNameW(path, buffer, buffer.Capacity);
        if (n > buffer.Capacity)
        {
            buffer = new StringBuilder((int)n + 1);
            n = GetLongPathNameW(path, buffer, buffer.Capacity);
        }
        return n == 0 || n > buffer.Capacity ? null : buffer.ToString();
    }

    /// <summary>Where a folder really is, with links and SUBST drives resolved; null if it cannot be opened.</summary>
    private static string? FinalPath(string folder)
    {
        using var handle = CreateFileW(folder, 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var buffer = new StringBuilder(512);
        var n = GetFinalPathNameByHandleW(handle, buffer, buffer.Capacity, 0);
        if (n > buffer.Capacity)
        {
            buffer = new StringBuilder((int)n + 1);
            n = GetFinalPathNameByHandleW(handle, buffer, buffer.Capacity, 0);
        }
        if (n == 0 || n > buffer.Capacity) return null;
        var result = buffer.ToString();
        if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + result[8..];
        if (result.StartsWith(@"\\?\", StringComparison.Ordinal)) return result[4..];
        return result;
    }

    private const uint FileShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, int pathLength, uint flags);
}
