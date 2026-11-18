using Microsoft.Win32;

namespace DiskTemizleyici.Core.Cleanup;

public enum CriticalKind
{
    None,

    /// <summary>The whole disk of a WSL distribution or of Docker Desktop.</summary>
    LinuxDisk,

    /// <summary>A virtual machine disk (Hyper-V, VMware, VirtualBox, QEMU).</summary>
    VirtualDisk,

    /// <summary>pagefile.sys, hiberfil.sys, swapfile.sys at the root of a drive.</summary>
    SystemFile,
}

/// <summary>
/// Recognises single files that are really a whole disk, or that Windows itself manages. They are often the biggest
/// files on a drive, and deleting one by mistake loses far more than its name suggests.
/// </summary>
public static class CriticalFiles
{
    private static readonly HashSet<string> DiskExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vhdx", ".vhd", ".avhdx", ".vmdk", ".vdi", ".qcow2",
    };

    private static readonly HashSet<string> SystemFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys",
    };

    private static readonly Lazy<List<string>> LinuxRoots = new(DetectLinuxRoots);

    public static CriticalKind Classify(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return CriticalKind.None;
        var name = Path.GetFileName(path);
        var parent = Path.GetDirectoryName(path);
        var root = Path.GetPathRoot(path);
        if (parent is not null && root is not null && SystemFileNames.Contains(name)
            && SafetyGuard.Normalize(parent) == SafetyGuard.Normalize(root))
            return CriticalKind.SystemFile;

        if (!DiskExtensions.Contains(Path.GetExtension(path))) return CriticalKind.None;
        var full = SafetyGuard.Normalize(path);
        return LinuxRoots.Value.Any(r => IsSameOrUnder(full, r)) ? CriticalKind.LinuxDisk : CriticalKind.VirtualDisk;
    }

    /// <summary>True if deleting <paramref name="folder"/> would delete a WSL or Docker disk.</summary>
    public static bool HoldsLinuxDisks(string folder)
    {
        var f = SafetyGuard.Normalize(folder);
        return LinuxRoots.Value.Any(r => IsSameOrUnder(r, f));
    }

    private static bool IsSameOrUnder(string path, string parent) =>
        path == parent || path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>Where WSL distributions and Docker Desktop keep their disks on this PC.</summary>
    private static List<string> DetectLinuxRoots()
    {
        var roots = new List<string>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
        {
            roots.Add(Path.Combine(local, "Docker", "wsl"));
            roots.Add(Path.Combine(local, "wsl"));
            try
            {
                foreach (var package in Directory.EnumerateDirectories(Path.Combine(local, "Packages"), "CanonicalGroupLimited.*"))
                    roots.Add(Path.Combine(package, "LocalState"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        try
        {
            using var lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            foreach (var id in lxss?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var distro = lxss!.OpenSubKey(id);
                if (distro?.GetValue("BasePath") is string basePath && !string.IsNullOrWhiteSpace(basePath))
                    roots.Add(basePath.StartsWith(@"\\?\", StringComparison.Ordinal) ? basePath[4..] : basePath);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return roots.Select(SafetyGuard.Normalize).Distinct().ToList();
    }
}
