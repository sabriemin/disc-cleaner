using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using DiskTemizleyici.App.Localization;
using Microsoft.Win32;

namespace DiskTemizleyici.App.Services;

/// <summary>
/// Writes crash reports and a small diagnostics log to %LOCALAPPDATA%\DiskTemizleyici\logs.
/// Nothing is ever sent anywhere. Profile paths and user names (of this account, of the signed-in account when the
/// app runs elevated under another one, and of every other profile on this PC) are replaced before writing, and the
/// computer name is never written.
/// </summary>
public static partial class CrashReporter
{
    private const int KeepReports = 10;
    private const long MaxDiagnosticsBytes = 512 * 1024;
    private static readonly object Gate = new();

    public static string LogFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskTemizleyici", "logs");

    public static string DiagnosticsPath => Path.Combine(LogFolder, "diagnostics.log");

    /// <summary>The app version with the commit it was built from, e.g. "0.2.0+3cff692…".</summary>
    public static string FullVersion =>
        System.Reflection.Assembly.GetEntryAssembly()
            ?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "unknown";

    /// <summary>Saves a crash report and returns its path (null if it could not be written).</summary>
    public static string? Write(Exception exception, string where)
    {
        string path;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogFolder);
                var text = new StringBuilder()
                    .AppendLine("Disk Temizleyici crash report")
                    .AppendLine($"Time:          {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
                    .AppendLine($"Version:       {FullVersion}")
                    .AppendLine($"Build:         {BuildKind()}")
                    .AppendLine($"Windows:       {Environment.OSVersion.Version} ({RuntimeInformation.OSArchitecture})")
                    .AppendLine($".NET:          {RuntimeInformation.FrameworkDescription}")
                    .AppendLine($"Language:      {Loc.I.Language}")
                    .AppendLine($"Administrator: {IsAdmin()}")
                    .AppendLine($"Glass style:   {App.Settings.Glass}")
                    .AppendLine($"Where:         {where}")
                    .AppendLine()
                    .AppendLine(exception.ToString())
                    .ToString();

                path = Path.Combine(LogFolder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.WriteAllText(path, Redact(text));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // Tidied on its own: a locked or read-only old report must not make the new one look unsaved.
        try
        {
            lock (Gate)
            {
                foreach (var old in Directory.GetFiles(LogFolder, "crash-*.txt").OrderByDescending(f => f).Skip(KeepReports))
                {
                    File.Delete(old);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        Diagnostic($"crash ({where}): {exception.GetType().Name}: {exception.Message}");
        return path;
    }

    /// <summary>Adds one line to the diagnostics log (scan summaries, rule and delete problems).</summary>
    public static void Diagnostic(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogFolder);
                var file = new FileInfo(DiagnosticsPath);
                if (file.Exists && file.Length > MaxDiagnosticsBytes)
                {
                    File.Move(DiagnosticsPath, DiagnosticsPath + ".old", overwrite: true);
                }
                File.AppendAllText(DiagnosticsPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {Redact(line)}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A GitHub "new issue" link that carries only the version, never anything from this computer.</summary>
    public static string IssueUrl() =>
        "https://github.com/sabriemin/disc-cleaner/issues/new?labels=bug&title=" +
        Uri.EscapeDataString($"Crash in {FullVersion.Split('+')[0]}");

    // ------------------------------------------------------------------------------------------------ redaction

    private sealed record Hidden(List<(string Path, string Replacement)> Paths, List<string> Names, string Machine);

    // Worked out once: the account names, the signed-in user and the profile list do not change while the app runs.
    private static readonly Lazy<Hidden> ToHide = new(FindWhatToHide);

    internal static string Redact(string text)
    {
        var hide = ToHide.Value;
        // Longest path first, own profile and the others together: C:\Users\Ann Lee must be replaced before C:\Users\Ann,
        // or "...\Ann Lee\..." is cut to "%USERPROFILE% Lee\..." and the rest of that other user's name is left in.
        foreach (var (path, replacement) in hide.Paths) text = ReplacePath(text, path, replacement);
        // anything else in a Users folder, such as files of an account that has no profile entry here
        text = UsersFolderEntry().Replace(text, m => IsSharedProfile(m.Groups[2].Value) ? m.Value : m.Groups[1].Value + "<user>");
        foreach (var name in hide.Names) text = ReplaceNameInPath(text, name, "<user>");
        if (hide.Machine.Length > 2) text = ReplaceWord(text, hide.Machine, "<pc>");
        return text;
    }

    /// <summary>Replaces a folder path only where it ends (C:\Users\Ann must not match inside C:\Users\Anna).</summary>
    private static string ReplacePath(string text, string path, string replacement) =>
        Regex.Replace(text, Regex.Escape(path) + @"(?=[\\/:'""\s]|$)", replacement.Replace("$", "$$"),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>
    /// Replaces an account name where it is a path segment or quoted ("C:\Users\Can\", "PC\Can", 'Can'), never inside
    /// words: an account called "Can" must not turn OperationCanceledException or "scan" into nonsense.
    /// </summary>
    private static string ReplaceNameInPath(string text, string name, string replacement) =>
        Regex.Replace(text, @"(?<=[\\/'""])" + Regex.Escape(name) + @"(?=[\\/:'""\s]|$)", replacement,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static string ReplaceWord(string text, string word, string replacement) =>
        Regex.Replace(text, @"(?<![\p{L}\p{N}_-])" + Regex.Escape(word) + @"(?![\p{L}\p{N}_-])", replacement,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsSharedProfile(string folder) =>
        folder.Equals("Public", StringComparison.OrdinalIgnoreCase)
        || folder.Equals("Default", StringComparison.OrdinalIgnoreCase)
        || folder.Equals("Default User", StringComparison.OrdinalIgnoreCase)
        || folder.Equals("All Users", StringComparison.OrdinalIgnoreCase);

    // "C:\Users\" and the folder name after it. The name stops at a single or double quote, so a quoted path such as
    // 'C:\Users\Default' ends at the quote (group 2 = "Default") instead of swallowing the rest of the line. It does NOT
    // stop at a space: a space is part of the name ("Default User", "All Users"), and stopping there would leak the rest
    // of a multi-word account name that has no profile entry (C:\Users\Maria Garcia -> only "Maria" redacted).
    [GeneratedRegex(@"(?i)\b([A-Z]:\\Users\\)([^\\/:*?""<>|\r\n']+)")]
    private static partial Regex UsersFolderEntry();

    private static Hidden FindWhatToHide()
    {
        var own = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var others = new List<string>();
        try
        {
            using var list = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (list is not null)
            {
                foreach (var sid in list.GetSubKeyNames())
                {
                    if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal)) continue; // people's accounts, not system ones
                    using var key = list.OpenSubKey(sid);
                    if (key?.GetValue("ProfileImagePath") is not string image) continue;
                    var profile = Path.TrimEndingDirectorySeparator(Environment.ExpandEnvironmentVariables(image));
                    if (profile.Length > 3 && !profile.Equals(own, StringComparison.OrdinalIgnoreCase)) others.Add(profile);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        // The own profile and the other profiles in one list, longest first, so a path that is another profile plus a
        // suffix (C:\Users\Ann Lee over C:\Users\Ann) is matched whole before the shorter one can cut into it.
        var paths = new List<(string Path, string Replacement)>();
        if (own.Length > 3) paths.Add((own, "%USERPROFILE%"));
        foreach (var profile in others) paths.Add((profile, "<profile>"));
        paths.Sort((a, b) => b.Path.Length.CompareTo(a.Path.Length));

        // This account, and the one signed in to Windows when "Run as administrator" used another account.
        var names = new List<string>();
        foreach (var name in new[] { Environment.UserName, SessionUser.SignedInUserName() })
        {
            if (!string.IsNullOrEmpty(name) && name.Length >= 2 && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        return new Hidden(paths, names, Environment.MachineName ?? "");
    }

    private static string BuildKind()
    {
        var runtime = RuntimeEnvironment.GetRuntimeDirectory();
        return runtime.Contains(@"\shared\Microsoft.NETCore.App\", StringComparison.OrdinalIgnoreCase) ? "framework-dependent" : "standalone";
    }

    private static bool IsAdmin()
    {
        try
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
