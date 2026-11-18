using DiskTemizleyici.Core.Cleanup;
using DiskTemizleyici.Core.Scanning;
using Xunit;

namespace DiskTemizleyici.Tests;

public class SafetyAndFeatureTests
{
    private static readonly string SystemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;

    [Theory]
    [InlineData("pagefile.sys", BlockReason.SystemFile)]
    [InlineData("hiberfil.sys", BlockReason.SystemFile)]
    [InlineData(@"Windows.old\Users\someone\Documents", BlockReason.PreviousWindows)]
    [InlineData(@"$Windows.~BT\Sources", BlockReason.SystemFolder)]
    [InlineData(@"Recovery\WindowsRE", BlockReason.SystemFolder)]
    [InlineData(@"System Volume Information\x", BlockReason.SystemFolder)]
    public void System_locations_are_blocked_with_the_right_reason(string relative, BlockReason expected)
    {
        Assert.False(SafetyGuard.CanDelete(Path.Combine(SystemRoot, relative), out var reason));
        Assert.Equal(expected, reason);
    }

    [Fact]
    public void Installer_package_caches_are_blocked()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.False(SafetyGuard.CanDelete(Path.Combine(programData, "Package Cache", "{guid}", "setup.msi"), out _));
        Assert.False(SafetyGuard.CanDelete(Path.Combine(local, "Package Cache", "x"), out _));
    }

    [Fact]
    public void Path_aliases_and_network_paths_are_blocked()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.False(SafetyGuard.CanDelete(@"\\?\" + Path.Combine(windows, "System32"), out _));
        Assert.False(SafetyGuard.CanDelete(@"\\localhost\C$\Windows", out var unc));
        Assert.Equal(BlockReason.NetworkPath, unc);
    }

    [Fact]
    public void A_junction_into_Windows_cannot_be_used_to_delete_Windows_files()
    {
        using var t = new TempDir();
        var link = t.Junction("win", Environment.GetFolderPath(Environment.SpecialFolder.Windows));

        Assert.False(SafetyGuard.CanDelete(Path.Combine(link, "System32"), out _));
        Assert.True(SafetyGuard.CanDelete(link, out _)); // removing the link itself is harmless
    }

    [Fact]
    public void Hidden_system_compatibility_junctions_are_kept()
    {
        using var t = new TempDir();
        var target = t.Dir("target");
        var link = t.Junction("Application Data", target);
        File.SetAttributes(link, File.GetAttributes(link) | FileAttributes.Hidden | FileAttributes.System);

        Assert.False(SafetyGuard.CanDelete(link, out var reason));
        Assert.Equal(BlockReason.SystemFolder, reason);
    }

    [Fact]
    public void Crash_dump_folders_under_Windows_may_be_cleaned_but_nothing_else_there()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.True(SafetyGuard.IsAllowedCleanupRoot(Path.Combine(windows, "Minidump")));
        Assert.True(SafetyGuard.IsAllowedCleanupRoot(Path.Combine(windows, "LiveKernelReports")));
        Assert.False(SafetyGuard.IsAllowedCleanupRoot(Path.Combine(windows, "WinSxS")));
        Assert.False(SafetyGuard.CanDelete(Path.Combine(windows, "Minidump"), out _)); // the folder itself stays
    }

    [Fact]
    public void Every_built_in_rule_path_points_to_a_place_that_may_be_cleaned()
    {
        foreach (var rule in RuleLoader.LoadBuiltIn().Where(r => r.Mode != RuleMode.RecycleBin))
        {
            foreach (var path in rule.Paths)
            {
                var sample = Environment.ExpandEnvironmentVariables(path.Replace("*", "sample"));
                Assert.True(SafetyGuard.IsAllowedCleanupRoot(sample), $"{rule.Id}: {sample}");
            }
        }
    }

    [Fact]
    public void Kernel_dump_rule_is_caution_and_keeps_recent_dumps()
    {
        var rule = RuleLoader.LoadBuiltIn().Single(r => r.Id == "kernel-crash-dumps");

        Assert.Equal(RiskLevel.Caution, rule.Risk);
        Assert.Equal(7, rule.MinAgeDays);
        Assert.Equal("*.dmp", rule.Pattern);
    }

    [Theory]
    [InlineData(@"D:\VMs\ubuntu.vhdx", CriticalKind.VirtualDisk)]
    [InlineData(@"D:\VMs\old.VMDK", CriticalKind.VirtualDisk)]
    [InlineData(@"C:\pagefile.sys", CriticalKind.SystemFile)]
    [InlineData(@"C:\folder\pagefile.sys", CriticalKind.None)]
    [InlineData(@"D:\movies\film.mkv", CriticalKind.None)]
    public void Whole_disk_and_system_files_are_recognised(string path, CriticalKind expected)
    {
        Assert.Equal(expected, CriticalFiles.Classify(path));
    }

    [Fact]
    public void Docker_and_WSL_disks_are_recognised_as_Linux_disks()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var docker = Path.Combine(local, "Docker", "wsl", "disk", "docker_data.vhdx");

        Assert.Equal(CriticalKind.LinuxDisk, CriticalFiles.Classify(docker));
        Assert.True(CriticalFiles.HoldsLinuxDisks(Path.Combine(local, "Docker")));
        Assert.False(CriticalFiles.HoldsLinuxDisks(Path.Combine(local, "Temp")));
    }

    [Fact]
    public void A_cancelled_delete_stops_and_says_so()
    {
        using var t = new TempDir();
        var a = t.File("a.bin", 1);
        var b = t.File("b.bin", 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var report = Deleter.Delete(new[] { a, b }, DeleteMode.Permanent, ct: cts.Token);

        Assert.True(report.Cancelled);
        Assert.Equal(0, report.ItemsDeleted);
        Assert.True(File.Exists(a) && File.Exists(b));
    }

    [Fact]
    public void Scan_counts_size_per_file_extension()
    {
        using var t = new TempDir();
        t.File("a.mp4", 100);
        t.File(@"sub\b.MP4", 50);
        t.File("c.txt", 10);
        t.File("README", 5);

        var types = new DiskScanner().Scan(t.Path).FileTypes;

        var mp4 = types.Single(s => s.Extension == ".mp4");
        Assert.Equal(150, mp4.Bytes);
        Assert.Equal(2, mp4.Count);
        Assert.Equal(FileCategory.Video, mp4.Category);
        Assert.Equal(5, types.Single(s => s.Extension == "").Bytes);
        Assert.Equal(".mp4", types[0].Extension); // largest first
    }

    [Theory]
    [InlineData(".mp4", FileCategory.Video)]
    [InlineData(".VHDX", FileCategory.DiskImage)]
    [InlineData(".msi", FileCategory.Installer)]
    [InlineData(".whatever", FileCategory.Other)]
    [InlineData("", FileCategory.Other)]
    public void Extensions_map_to_categories(string extension, FileCategory expected)
    {
        Assert.Equal(expected, FileCategories.Of(extension));
    }
}
