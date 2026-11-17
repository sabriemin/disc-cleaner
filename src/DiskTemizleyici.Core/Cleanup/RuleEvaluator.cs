namespace DiskTemizleyici.Core.Cleanup;

/// <summary>What one rule would delete on this computer right now.</summary>
public sealed class CleanupCandidate
{
    public required CleanupRule Rule { get; init; }

    /// <summary>Folders the rule matched on this computer.</summary>
    public required List<string> Roots { get; init; }

    /// <summary>Exact files and folders that would be deleted.</summary>
    public required List<string> Items { get; init; }

    public required long Size { get; init; }

    public required long FileCount { get; init; }
}

public static class RuleEvaluator
{
    /// <summary>Works out what a rule would delete. Returns null when nothing matches.</summary>
    public static CleanupCandidate? Evaluate(CleanupRule rule, DateTime? nowUtc = null, CancellationToken ct = default)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        return rule.Mode switch
        {
            RuleMode.RecycleBin => EvaluateRecycleBin(rule),
            RuleMode.Contents => EvaluateContents(rule, now, ct),
            RuleMode.Files => EvaluateFiles(rule, now, ct),
            _ => null,
        };
    }

    public static List<CleanupCandidate> EvaluateAll(IEnumerable<CleanupRule> rules, CancellationToken ct = default)
    {
        var list = new List<CleanupCandidate>();
        foreach (var rule in rules)
        {
            ct.ThrowIfCancellationRequested();
            var c = Evaluate(rule, ct: ct);
            if (c is not null && c.Size > 0) list.Add(c);
        }
        return list.OrderByDescending(c => c.Size).ToList();
    }

    private static CleanupCandidate? EvaluateRecycleBin(CleanupRule rule)
    {
        var (size, count) = RecycleBin.Query();
        if (size <= 0) return null;
        return new CleanupCandidate { Rule = rule, Roots = new(), Items = new(), Size = size, FileCount = count };
    }

    private static CleanupCandidate? EvaluateContents(CleanupRule rule, DateTime now, CancellationToken ct)
    {
        var roots = rule.Paths.SelectMany(PathExpander.Expand).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (roots.Count == 0) return null;

        var cutoff = now.AddDays(-rule.MinAgeDays);
        var items = new List<string>();
        long size = 0, files = 0;

        foreach (var root in roots)
        {
            if (!SafetyGuard.IsAllowedCleanupRoot(root)) continue;
            foreach (var entry in SafeEnumerate(root))
            {
                ct.ThrowIfCancellationRequested();
                if (rule.MinAgeDays > 0 && entry.LastWriteTimeUtc > cutoff) continue;
                if (entry is DirectoryInfo d)
                {
                    if (d.LinkTarget is not null) { items.Add(d.FullName); continue; } // link: deleting removes only the link
                    var (s, f) = MeasureFolder(d.FullName, ct);
                    size += s; files += f;
                }
                else if (entry is FileInfo fi)
                {
                    size += fi.Length; files++;
                }
                items.Add(entry.FullName);
            }
        }

        if (items.Count == 0) return null;
        return new CleanupCandidate { Rule = rule, Roots = roots, Items = items, Size = size, FileCount = files };
    }

    private static CleanupCandidate? EvaluateFiles(CleanupRule rule, DateTime now, CancellationToken ct)
    {
        var roots = rule.Paths.SelectMany(PathExpander.Expand).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (roots.Count == 0) return null;

        var pattern = string.IsNullOrWhiteSpace(rule.Pattern) ? "*" : rule.Pattern;
        var cutoff = now.AddDays(-rule.MinAgeDays);
        var items = new List<string>();
        long size = 0;

        foreach (var root in roots)
        {
            if (!SafetyGuard.IsAllowedCleanupRoot(root)) continue;
            var matches = new DirectoryInfo(root)
                .EnumerateFiles(pattern, new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true })
                .ToList();

            // save1.cok + save1.cok.cid belong together: group by folder + name before the first dot.
            var groups = matches
                .GroupBy(f => Path.Combine(f.DirectoryName ?? "", StemOf(f.Name)), StringComparer.OrdinalIgnoreCase)
                .Select(g => (Files: g.ToList(), Newest: g.Max(f => f.LastWriteTimeUtc)))
                .OrderByDescending(g => g.Newest)
                .Skip(Math.Max(0, rule.KeepNewest));

            foreach (var g in groups)
            {
                ct.ThrowIfCancellationRequested();
                if (rule.MinAgeDays > 0 && g.Newest > cutoff) continue;
                foreach (var f in g.Files)
                {
                    items.Add(f.FullName);
                    size += f.Length;
                }
            }
        }

        if (items.Count == 0) return null;
        return new CleanupCandidate { Rule = rule, Roots = roots, Items = items, Size = size, FileCount = items.Count };
    }

    internal static string StemOf(string fileName)
    {
        var dot = fileName.IndexOf('.');
        return dot <= 0 ? fileName : fileName[..dot];
    }

    private static IEnumerable<FileSystemInfo> SafeEnumerate(string dir)
    {
        try
        {
            return new DirectoryInfo(dir)
                .EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<FileSystemInfo>();
        }
    }

    internal static (long Size, long Files) MeasureFolder(string dir, CancellationToken ct)
    {
        long size = 0, files = 0;
        try
        {
            var opts = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                AttributesToSkip = 0,
            };
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opts))
            {
                ct.ThrowIfCancellationRequested();
                size += f.Length;
                files++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return (size, files);
    }
}
