using DiskTemizleyici.Core.Cleanup;

namespace DiskTemizleyici.Core.Scanning;

/// <summary>
/// Made-up scan results for screenshots and UI work, so that no real user's folders, names or files
/// ever end up in a picture. Enabled in the app with the DISKTEMIZLEYICI_DEMO environment variable.
/// </summary>
public static class DemoData
{
    private const long GB = 1024L * 1024 * 1024;
    private const long MB = 1024L * 1024;

    public static ScanResult CreateScan()
    {
        var root = new DirNode(@"C:\", null);
        root.OwnFilesSize = 22 * GB; // pagefile.sys + hiberfil.sys
        var rootFiles = 3;

        var users = Dir(root, "Users");
        var you = Dir(users, "you");
        var appData = Dir(you, "AppData");
        var local = Dir(appData, "Local");
        Leaf(local, "Packages", 25.9, 120_006);
        Leaf(local, "NVIDIA", 10.1, 585);
        Leaf(local, "npm-cache", 7.03, 119_600);
        Leaf(local, "Temp", 6.56, 25_665);
        Leaf(local, "Docker", 5.35, 93);
        Leaf(local, "Google", 1.91, 4_273);
        Leaf(appData, "Roaming", 3.2, 20_114);
        var lowLocal = Dir(appData, "LocalLow");
        Leaf(lowLocal, "Colossal Order", 38.4, 3_991);
        Leaf(you, "Videos", 22.6, 128);
        Leaf(you, "Downloads", 14.9, 912);
        Leaf(you, "Documents", 9.4, 5_204);
        Leaf(you, "Pictures", 1.4, 2_310);
        var oneDrive = Leaf(you, "OneDrive", 0.3, 410);
        oneDrive.CloudOnlySize = 78 * GB;
        oneDrive.CloudOnlyCount = 49_211;
        Leaf(Dir(users, "Public"), "Documents", 0.02, 12);

        Leaf(root, "Program Files", 69.1, 301_224);
        var x86 = Dir(root, "Program Files (x86)");
        Leaf(x86, "Steam", 51.2, 60_418);
        Leaf(x86, "Microsoft", 8.05, 12_093);
        Leaf(x86, "Common Files", 0.7, 3_101);
        Leaf(root, "Windows", 47.0, 402_331);
        Leaf(root, "ProgramData", 25.9, 51_200);
        Leaf(root, "XboxGames", 15.3, 2_031);
        Leaf(root, "$Recycle.Bin", 5.24, 35);
        var docsAndSettings = new DirNode(@"C:\Documents and Settings", root) { IsLink = true };
        root.AddChild(docsAndSettings);

        Finish(root);
        root.FileCount += rootFiles;

        var files = new List<LargeFile>
        {
            new(@"C:\pagefile.sys", 16 * GB, DateTime.UtcNow.AddHours(-2)),
            new(@"C:\Users\you\Videos\holiday-2025.mp4", (long)(8.4 * GB), DateTime.UtcNow.AddDays(-120)),
            new(@"C:\hiberfil.sys", (long)(6.3 * GB), DateTime.UtcNow.AddDays(-1)),
            new(@"C:\Users\you\Downloads\ubuntu-24.04-desktop-amd64.iso", (long)(5.7 * GB), DateTime.UtcNow.AddDays(-200)),
            new(@"C:\Users\you\AppData\Local\Docker\wsl\disk\docker_data.vhdx", (long)(5.2 * GB), DateTime.UtcNow.AddDays(-6)),
            new(@"C:\Program Files (x86)\Steam\steamapps\common\SomeGame\data0.pak", (long)(4.1 * GB), DateTime.UtcNow.AddDays(-40)),
            new(@"C:\Users\you\Documents\backup-2025.zip", (long)(3.2 * GB), DateTime.UtcNow.AddDays(-300)),
            new(@"C:\Users\you\Videos\screen-recording-04.mkv", (long)(2.9 * GB), DateTime.UtcNow.AddDays(-15)),
            new(@"C:\Users\you\AppData\Local\Android\Sdk\system-images\android-35\system.img", (long)(2.35 * GB), DateTime.UtcNow.AddDays(-90)),
            new(@"C:\Users\you\Downloads\project-assets-final-v3.zip", (long)(1.8 * GB), DateTime.UtcNow.AddDays(-33)),
            new(@"C:\Users\you\AppData\Local\NVIDIA\DXCache\b6f2a998eacea347.nvph", 1 * GB, DateTime.UtcNow.AddDays(-12)),
            new(@"C:\Users\you\Downloads\VS_Setup_offline.zip", 870 * MB, DateTime.UtcNow.AddDays(-75)),
        };

        return new ScanResult
        {
            Root = root,
            LargestFiles = files,
            Elapsed = TimeSpan.FromSeconds(53.2),
            DirectoriesScanned = 412_337,
            InaccessibleDirectories = 0,
        };
    }

    public static List<CleanupCandidate> CreateCandidates(IEnumerable<CleanupRule> rules)
    {
        var byId = rules.ToDictionary(r => r.Id);
        var list = new List<CleanupCandidate>();

        void Add(string id, double gb, long files, params string[] roots)
        {
            if (!byId.TryGetValue(id, out var rule)) return;
            list.Add(new CleanupCandidate
            {
                Rule = rule,
                Roots = roots.ToList(),
                Items = Enumerable.Range(0, (int)Math.Min(files, 50)).Select(i => $@"demo\{id}\{i}").ToList(),
                Size = (long)(gb * GB),
                FileCount = files,
            });
        }

        Add("cs2-autosaves", 38.4, 3_982, @"C:\Users\you\AppData\LocalLow\Colossal Order\Cities Skylines II\Saves");
        Add("shader-cache", 10.1, 919, @"C:\Users\you\AppData\Local\D3DSCache", @"C:\Users\you\AppData\Local\NVIDIA\DXCache");
        Add("npm-cache", 7.03, 119_600, @"C:\Users\you\AppData\Local\npm-cache");
        Add("recycle-bin", 5.24, 35);
        Add("user-temp", 5.15, 17_409, @"C:\Users\you\AppData\Local\Temp");
        Add("windows-update-download", 2.3, 214, @"C:\Windows\SoftwareDistribution\Download");
        Add("browser-cache", 0.6, 8_210, @"C:\Users\you\AppData\Local\Google\Chrome\User Data\Default\Cache");
        Add("crash-dumps", 0.15, 513, @"C:\Users\you\AppData\Local\CrashDumps");
        return list.OrderByDescending(c => c.Size).ToList();
    }

    private static DirNode Dir(DirNode parent, string name)
    {
        var node = new DirNode(Path.Combine(parent.FullPath, name), parent);
        parent.AddChild(node);
        return node;
    }

    private static DirNode Leaf(DirNode parent, string name, double gb, long files)
    {
        var node = Dir(parent, name);
        node.OwnFilesSize = (long)(gb * GB);
        node.FileCount = files;
        return node;
    }

    /// <summary>Adds sizes up the tree the same way the scanner does.</summary>
    private static void Finish(DirNode node)
    {
        long size = node.OwnFilesSize, files = node.FileCount, cloud = node.CloudOnlySize, cloudFiles = node.CloudOnlyCount;
        foreach (var child in node.Children)
        {
            Finish(child);
            size += child.Size;
            files += child.FileCount;
            cloud += child.CloudOnlySize;
            cloudFiles += child.CloudOnlyCount;
        }
        node.Size = size;
        node.FileCount = files;
        node.CloudOnlySize = cloud;
        node.CloudOnlyCount = cloudFiles;
        node.SortChildren();
    }
}
