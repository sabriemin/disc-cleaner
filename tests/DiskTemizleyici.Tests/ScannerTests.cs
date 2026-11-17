using System.Diagnostics;
using DiskTemizleyici.Core.Scanning;
using Xunit;

namespace DiskTemizleyici.Tests;

public class ScannerTests
{
    [Fact]
    public void Sizes_add_up_through_the_tree_and_children_are_sorted()
    {
        using var t = new TempDir();
        t.File("a.bin", 100);
        t.File(@"small\x.bin", 10);
        t.File(@"big\y.bin", 1000);
        t.File(@"big\deep\z.bin", 500);

        var result = new DiskScanner().Scan(t.Path);

        Assert.Equal(1610, result.Root.Size);
        Assert.Equal(100, result.Root.OwnFilesSize);
        Assert.Equal(4, result.Root.FileCount);
        Assert.Equal(new[] { "big", "small" }, result.Root.Children.Select(c => c.Name));
        Assert.Equal(1500, result.Root.Children[0].Size);
        Assert.Equal("y.bin", Path.GetFileName(result.LargestFiles[0].FullPath));
    }

    [Fact]
    public void Junctions_are_not_followed()
    {
        using var t = new TempDir();
        t.File(@"real\data.bin", 2000);
        var link = Path.Combine(t.Path, "link");
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Path.Combine(t.Path, "real")}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "could not create a junction for the test");

        var result = new DiskScanner().Scan(t.Path);

        Assert.Equal(2000, result.Root.Size); // counted once, not twice
        Assert.True(result.Root.Children.Single(c => c.Name == "link").IsLink);
    }

    [Fact]
    public void Removing_a_child_updates_ancestors()
    {
        using var t = new TempDir();
        t.File(@"a\b\c.bin", 300);
        t.File(@"a\d.bin", 50);
        var root = new DiskScanner().Scan(t.Path).Root;
        var a = root.Children.Single();
        var b = a.Children.Single();

        a.RemoveChild(b);

        Assert.Equal(50, a.Size);
        Assert.Equal(50, root.Size);
        Assert.Same(a, root.Find(Path.Combine(t.Path, "a")));
    }

    [Fact]
    public void Largest_files_keeps_only_the_top_n()
    {
        using var t = new TempDir();
        for (var i = 1; i <= 20; i++) t.File($"f{i}.bin", i * 10);

        var result = new DiskScanner { LargeFileCount = 5 }.Scan(t.Path);

        Assert.Equal(new long[] { 200, 190, 180, 170, 160 }, result.LargestFiles.Select(f => f.Size));
    }
}
