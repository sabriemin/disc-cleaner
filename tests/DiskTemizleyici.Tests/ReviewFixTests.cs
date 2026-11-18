using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DiskTemizleyici.Core.Cleanup;
using DiskTemizleyici.Core.Scanning;
using Xunit;

namespace DiskTemizleyici.Tests;

/// <summary>Cases found in the review of the 0.2 changes.</summary>
public class ReviewFixTests
{
    private static CleanupRule Rule(RuleMode mode, string path, string? pattern = null, int minAge = 0) => new()
    {
        Id = "test",
        Name = new() { ["en"] = "Test" },
        Description = new() { ["en"] = "" },
        Mode = mode,
        Paths = new() { path },
        Pattern = pattern,
        MinAgeDays = minAge,
    };

    private static void SetFolderTimes(string folder, DateTime utc)
    {
        Directory.SetCreationTimeUtc(folder, utc);
        Directory.SetLastWriteTimeUtc(folder, utc);
    }

    /// <summary>Takes away the right to list <paramref name="folder"/> from everyone; undone by <see cref="Allow"/>.</summary>
    private static void Deny(string folder) => Icacls($"\"{folder}\" /deny *S-1-1-0:(RD)");

    private static void Allow(string folder) => Icacls($"\"{folder}\" /remove:d *S-1-1-0");

    private static void Icacls(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo("icacls.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("icacls failed: " + p.StandardError.ReadToEnd());
    }

    // ------------------------------------------------------------------------------------------ deleting

    [Fact]
    public void A_folder_that_changed_after_the_suggestion_is_left_alone()
    {
        using var t = new TempDir();
        var old = DateTime.UtcNow.AddDays(-10);
        t.File(@"setup\a.log", 10, old);
        var folder = Path.Combine(t.Path, "setup");
        SetFolderTimes(folder, old);

        var c = RuleEvaluator.Evaluate(Rule(RuleMode.Contents, t.Path, minAge: 1))!;
        Assert.Contains(folder, c.Items);

        t.File(@"setup\written-later.log", 10); // a program writes into it before the user clicks Clean
        var report = Deleter.Delete(c.Items, DeleteMode.Permanent,
            options: new DeleteOptions(c.Roots, DateTime.UtcNow.AddDays(-1)));

        Assert.Equal(1, report.ItemsChanged);
        Assert.Equal(0, report.ItemsDeleted);
        Assert.True(File.Exists(Path.Combine(folder, "written-later.log")));
    }

    [Fact]
    public void An_item_moved_behind_a_junction_after_the_check_is_not_deleted()
    {
        using var t = new TempDir();
        var root = t.Dir("temp");
        var item = t.File(@"temp\sub\x.tmp", 10);
        var elsewhere = t.File(@"elsewhere\x.tmp", 10);

        // after the suggestion was worked out, "sub" is swapped for a link to somewhere else
        Directory.Delete(Path.Combine(root, "sub"), recursive: true);
        t.Junction(@"temp\sub", Path.GetDirectoryName(elsewhere)!);

        var report = Deleter.Delete(new[] { item }, DeleteMode.Permanent, options: new DeleteOptions(new[] { root }));

        Assert.Equal(1, report.ItemsBlocked);
        Assert.Equal(BlockReason.RedirectedPath, report.Problems.Single().BlockReason);
        Assert.True(File.Exists(elsewhere));
    }

    [Fact]
    public void An_item_that_is_already_gone_counts_as_missing()
    {
        using var t = new TempDir();
        var report = Deleter.Delete(new[] { Path.Combine(t.Path, "never-there.tmp") }, DeleteMode.Permanent);

        Assert.Equal(1, report.ItemsMissing);
        Assert.Empty(report.Problems);
    }

    [Fact]
    public void A_cleanup_root_that_is_a_link_is_checked_where_it_points()
    {
        using var t = new TempDir();
        var toDrive = t.Junction("cache-on-drive-root", Path.GetPathRoot(Environment.SystemDirectory)!);
        var toFolder = t.Junction("cache-on-other-folder", t.Dir("real-cache"));

        Assert.False(SafetyGuard.IsAllowedCleanupRoot(toDrive));
        Assert.True(SafetyGuard.IsAllowedCleanupRoot(toFolder));
    }

    [Theory]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\Users")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\Windows\System32")]
    [InlineData(@"Users\someone")]
    public void Paths_without_a_drive_letter_are_refused(string path)
    {
        Assert.False(SafetyGuard.CanDelete(path, out var reason));
        Assert.Equal(BlockReason.InvalidPath, reason);
    }

    // ------------------------------------------------------------------------------------------ evaluating

    [Fact]
    public void An_old_folder_with_an_unreadable_part_is_not_taken_whole()
    {
        using var t = new TempDir();
        var old = DateTime.UtcNow.AddDays(-10);
        var oldFile = t.File(@"installer\old.log", 10, old);
        var locked = t.Dir(@"installer\locked");
        t.File(@"installer\locked\new.log", 10); // recent, but nobody can see it
        SetFolderTimes(locked, old);
        var folder = Path.Combine(t.Path, "installer");
        SetFolderTimes(folder, old);

        Deny(locked);
        try
        {
            Assert.False(RuleEvaluator.Measure(folder, default).Complete);

            var c = RuleEvaluator.Evaluate(Rule(RuleMode.Contents, t.Path, minAge: 1))!;

            Assert.DoesNotContain(folder, c.Items);
            Assert.Contains(oldFile, c.Items);
            Assert.True(c.AccessDenied);
            Assert.Null(c.Error);
        }
        finally
        {
            Allow(locked);
        }
    }

    [Fact]
    public void An_unreadable_folder_in_a_Files_rule_is_reported_not_an_error()
    {
        using var t = new TempDir();
        var old = DateTime.UtcNow.AddDays(-30);
        var visible = t.File("crash1.dmp", 10, old);
        var locked = t.Dir("locked");
        t.File(@"locked\crash2.dmp", 10, old);

        Deny(locked);
        try
        {
            var c = RuleEvaluator.Evaluate(Rule(RuleMode.Files, t.Path, "*.dmp", minAge: 7))!;

            Assert.Null(c.Error);
            Assert.True(c.AccessDenied);
            Assert.Equal(new[] { visible }, c.Items);
        }
        finally
        {
            Allow(locked);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint size);

    [Fact]
    public void Rule_folders_given_as_short_names_come_back_as_long_names()
    {
        using var t = new TempDir();
        var longFolder = t.Dir("A long folder name");
        var buffer = new StringBuilder(1024);
        if (GetShortPathNameW(longFolder, buffer, (uint)buffer.Capacity) == 0) return;
        var shortFolder = buffer.ToString();
        if (string.Equals(shortFolder, longFolder, StringComparison.OrdinalIgnoreCase)) return; // 8.3 names are off on this drive

        var found = PathExpander.Expand(shortFolder).Single();

        Assert.Equal(longFolder, found, ignoreCase: true);
    }

    [Fact]
    public void An_empty_entry_in_a_local_rules_file_is_skipped()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "rules.json");
        File.WriteAllText(file, """
            [
              null,
              { "id": "my-cache", "name": { "en": "Mine" }, "description": { "en": "Mine" },
                "mode": "Contents", "risk": "Safe", "paths": [ "%TEMP%\\my-cache" ] }
            ]
            """);
        var problems = new List<string>();

        var rules = RuleLoader.LoadAll(file, problems);

        Assert.Contains(rules, r => r.Id == "my-cache");
        Assert.Contains(rules, r => r.Id == "user-temp"); // the built-in rules are all still there
        Assert.Single(problems);
    }

    // ------------------------------------------------------------------------------------------ the tree

    [Fact]
    public void A_late_refresh_of_a_replaced_folder_does_not_change_the_live_tree()
    {
        using var t = new TempDir();
        t.File(@"a\b\big.bin", 1000);
        var root = new DiskScanner().Scan(t.Path).Root;
        var a = root.Children.Single();
        var b = a.Children.Single();
        var scanner = new DiskScanner();

        var lateB = scanner.ScanDetached(b); // started first...
        File.Delete(Path.Combine(t.Path, @"a\b\big.bin"));
        Assert.True(root.ReplaceChild(a, scanner.ScanDetached(a))); // ...but the parent's refresh finishes first
        Assert.Equal(0, root.Size);

        Assert.False(a.ReplaceChild(b, lateB)); // a is no longer in the tree
        Assert.Equal(0, root.Size);
        Assert.False(b.IsAttached);
    }

    [Fact]
    public void A_deleted_file_is_taken_out_of_its_folder_and_the_ones_above()
    {
        using var t = new TempDir();
        t.File(@"a\big.bin", 1000);
        t.File(@"a\small.bin", 10);
        var root = new DiskScanner().Scan(t.Path).Root;
        var a = root.Children.Single();

        Assert.True(a.RemoveOwnFile(1000));

        Assert.Equal(10, a.Size);
        Assert.Equal(10, a.OwnFilesSize);
        Assert.Equal(1, a.FileCount);
        Assert.Equal(10, root.Size);
        Assert.Equal(1, root.FileCount);
    }

    [Theory]
    [InlineData("film.MP4", ".mp4")]
    [InlineData("backup.2024-11-18_full", "")]
    [InlineData("README", "")]
    public void Files_are_grouped_by_the_same_extension_everywhere(string name, string expected)
    {
        Assert.Equal(expected, FileCategories.ExtensionOf(name));
    }

    // -------------------------------------------------------------------------------- second-round review

    [Fact]
    public void A_confined_permanent_delete_removes_a_normal_folder()
    {
        using var t = new TempDir();
        var root = t.Dir("cache");
        t.File(@"cache\keep.tmp", 10);
        t.File(@"cache\sub\a.tmp", 10);
        t.File(@"cache\sub\deep\b.tmp", 10);
        var sub = Path.Combine(root, "sub");

        var report = Deleter.Delete(new[] { sub }, DeleteMode.Permanent, options: new DeleteOptions(new[] { root }));

        Assert.Equal(1, report.ItemsDeleted);
        Assert.Empty(report.Problems);
        Assert.False(Directory.Exists(sub));
        Assert.True(File.Exists(Path.Combine(root, "keep.tmp"))); // the rest of the confined root is untouched
    }

    [Fact]
    public void A_folder_that_cannot_be_read_at_all_is_reported_not_queued()
    {
        using var t = new TempDir();
        var locked = t.Dir("locked");
        t.File(@"locked\secret.dat", 10);
        t.File(@"readable\keep.dat", 10);

        Deny(locked);
        try
        {
            var c = RuleEvaluator.Evaluate(Rule(RuleMode.Contents, t.Path))!; // no age limit, whole-folder items

            Assert.True(c.AccessDenied);
            Assert.Null(c.Error);
            Assert.DoesNotContain(Path.Combine(t.Path, "locked"), c.Items); // would only fail on delete
            Assert.Contains(Path.Combine(t.Path, "readable"), c.Items);
        }
        finally
        {
            Allow(locked);
        }
    }

    [Fact]
    public void A_newly_scanned_folder_is_added_to_the_tree_and_its_size_flows_up()
    {
        using var t = new TempDir();
        t.File(@"a\x.bin", 100);
        var root = new DiskScanner().Scan(t.Path).Root;
        var rootSize = root.Size;

        var fresh = new DirNode(Path.Combine(t.Path, "bin"), root) { Size = 500, FileCount = 1 };
        Assert.True(root.AddScannedChild(fresh));

        Assert.Equal(rootSize + 500, root.Size);
        Assert.Contains(root.Children, c => ReferenceEquals(c, fresh));
        Assert.False(root.AddScannedChild(fresh)); // a child with that path is already here
    }
}
