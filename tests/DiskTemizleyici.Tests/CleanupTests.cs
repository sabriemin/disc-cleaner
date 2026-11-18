using DiskTemizleyici.Core.Cleanup;
using Xunit;

namespace DiskTemizleyici.Tests;

public class CleanupTests
{
    private static CleanupRule Rule(RuleMode mode, string path, string? pattern = null, int keep = 0, int minAge = 0) => new()
    {
        Id = "test",
        Name = new() { ["en"] = "Test" },
        Description = new() { ["en"] = "" },
        Mode = mode,
        Paths = new() { path },
        Pattern = pattern,
        KeepNewest = keep,
        MinAgeDays = minAge,
    };

    [Fact]
    public void Built_in_rules_load_and_have_both_languages()
    {
        var rules = RuleLoader.LoadBuiltIn();

        Assert.NotEmpty(rules);
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
        foreach (var r in rules)
        {
            Assert.True(r.Name.ContainsKey("tr") && r.Name.ContainsKey("en"), r.Id);
            Assert.True(r.Description.ContainsKey("tr") && r.Description.ContainsKey("en"), r.Id);
            if (r.Mode != RuleMode.RecycleBin) Assert.NotEmpty(r.Paths);
        }
    }

    [Fact]
    public void Files_mode_keeps_the_newest_saves_and_their_companion_files()
    {
        using var t = new TempDir();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 6; i++)
        {
            var time = now.AddHours(-i);
            t.File($"AutoSave{i}.cok", 100, time);
            t.File($"AutoSave{i}.cok.cid", 1, time);
        }
        t.File("MyCity.cok", 100, now.AddDays(-30)); // manual save: never matches

        var c = RuleEvaluator.Evaluate(Rule(RuleMode.Files, t.Path, "AutoSave*", keep: 2))!;

        Assert.Equal(8, c.Items.Count); // 4 oldest saves × 2 files
        Assert.Equal(4 * 101, c.Size);
        Assert.DoesNotContain(c.Items, p => p.EndsWith("AutoSave0.cok") || p.EndsWith("AutoSave1.cok.cid"));
        Assert.DoesNotContain(c.Items, p => p.EndsWith("MyCity.cok"));
    }

    [Fact]
    public void Contents_mode_respects_min_age_and_measures_folders()
    {
        using var t = new TempDir();
        var old = DateTime.UtcNow.AddDays(-10);
        t.File("old.tmp", 100, old);
        t.File("new.tmp", 100);
        t.File(@"olddir\inner.tmp", 300, old);
        Directory.SetCreationTimeUtc(Path.Combine(t.Path, "olddir"), old);
        Directory.SetLastWriteTimeUtc(Path.Combine(t.Path, "olddir"), old);

        var c = RuleEvaluator.Evaluate(Rule(RuleMode.Contents, t.Path, minAge: 1))!;

        Assert.Equal(2, c.Items.Count);
        Assert.Equal(400, c.Size);
        Assert.DoesNotContain(c.Items, p => p.EndsWith("new.tmp"));
    }

    [Fact]
    public void Wildcard_segments_expand_to_every_matching_folder()
    {
        using var t = new TempDir();
        t.File(@"Profile 1\Cache\a", 10);
        t.File(@"Default\Cache\b", 10);
        t.File(@"Default\Other\c", 10);

        var found = PathExpander.Expand(Path.Combine(t.Path, "*", "Cache")).ToList();

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Permanent_delete_removes_items_and_reports_them()
    {
        using var t = new TempDir();
        var f = t.File("x.bin", 10);
        t.File(@"d\y.bin", 10);
        var d = Path.Combine(t.Path, "d");

        var report = Deleter.Delete(new[] { f, d }, DeleteMode.Permanent);

        Assert.Equal(2, report.ItemsDeleted);
        Assert.False(File.Exists(f));
        Assert.False(Directory.Exists(d));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Windows\WinSxS\Temp")]
    [InlineData(@"C:\Program Files")]
    public void Safety_guard_blocks_system_locations(string path)
    {
        Assert.False(SafetyGuard.CanDelete(path, out _));
    }

    [Fact]
    public void Safety_guard_blocks_profile_folders_but_allows_their_contents()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        Assert.False(SafetyGuard.CanDelete(profile, out _));
        Assert.False(SafetyGuard.CanDelete(docs, out _));
        Assert.True(SafetyGuard.CanDelete(Path.Combine(docs, "old-project"), out _));
        Assert.True(SafetyGuard.CanDelete(@"C:\Windows\Temp\setup.log", out _));
    }

    [Fact]
    public void Blocked_paths_are_never_deleted()
    {
        var report = Deleter.Delete(new[] { @"C:\Windows" }, DeleteMode.Permanent);

        Assert.Equal(1, report.ItemsBlocked);
        Assert.Equal(0, report.ItemsDeleted);
        Assert.True(Directory.Exists(@"C:\Windows"));
    }

    [Fact]
    public void Stem_groups_save_with_companion()
    {
        Assert.Equal("AutoSave1", RuleEvaluator.StemOf("AutoSave1.cok.cid"));
        Assert.Equal("noext", RuleEvaluator.StemOf("noext"));
    }
}
