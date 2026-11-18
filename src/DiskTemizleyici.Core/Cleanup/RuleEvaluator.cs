using System.Collections.Concurrent;
using System.IO.Enumeration;

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

    /// <summary>A folder of this rule exists but could not be read (usually it needs "Run as administrator").</summary>
    public bool AccessDenied { get; init; }

    /// <summary>Set when the rule could not be checked at all.</summary>
    public string? Error { get; init; }

    /// <summary>True when there is something that can actually be cleaned.</summary>
    public bool IsActionable => Error is null && (Rule.Mode == RuleMode.RecycleBin ? Size > 0 : Items.Count > 0);
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

    /// <summary>
    /// Evaluates the rules in parallel and reports each useful result as soon as it is ready. A rule that fails is
    /// reported with <see cref="CleanupCandidate.Error"/> instead of stopping the others.
    /// </summary>
    public static Task EvaluateAllAsync(IEnumerable<CleanupRule> rules, Action<CleanupCandidate> found,
        CancellationToken ct = default, int parallelism = 4)
    {
        return Parallel.ForEachAsync(rules, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = parallelism },
            (rule, token) =>
            {
                var candidate = EvaluateSafely(rule, DateTime.UtcNow, token);
                // Report what frees space, plus rules that need attention; empty folders alone are not worth a card.
                if (candidate is not null && (candidate.Size > 0 || candidate.AccessDenied || candidate.Error is not null))
                    found(candidate);
                return ValueTask.CompletedTask;
            });
    }

    /// <summary>Synchronous version of <see cref="EvaluateAllAsync"/>, largest first.</summary>
    public static List<CleanupCandidate> EvaluateAll(IEnumerable<CleanupRule> rules, CancellationToken ct = default)
    {
        var results = new ConcurrentBag<CleanupCandidate>();
        EvaluateAllAsync(rules, results.Add, ct).GetAwaiter().GetResult();
        return results.OrderByDescending(c => c.Size).ToList();
    }

    internal static CleanupCandidate? EvaluateSafely(CleanupRule rule, DateTime now, CancellationToken ct)
    {
        try
        {
            return Evaluate(rule, now, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CleanupCandidate { Rule = rule, Roots = new(), Items = new(), Size = 0, FileCount = 0, Error = ex.Message };
        }
    }

    private static CleanupCandidate? EvaluateRecycleBin(CleanupRule rule)
    {
        var (size, count) = RecycleBin.Query();
        if (size <= 0) return null;
        return new CleanupCandidate { Rule = rule, Roots = new(), Items = new(), Size = size, FileCount = count };
    }

    private static List<string> RootsOf(CleanupRule rule) =>
        (rule.Paths ?? throw new InvalidOperationException("The rule has no paths."))
        .SelectMany(PathExpander.Expand)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static CleanupCandidate? EvaluateContents(CleanupRule rule, DateTime now, CancellationToken ct)
    {
        var roots = RootsOf(rule);
        if (roots.Count == 0) return null;

        var useAge = rule.MinAgeDays > 0;
        var cutoff = now.AddDays(-rule.MinAgeDays);
        var items = new List<string>();
        long size = 0, files = 0;
        var denied = false;

        foreach (var root in roots)
        {
            if (!SafetyGuard.IsAllowedCleanupRoot(root)) continue;
            var listing = List(root, out var entries);
            if (listing == Listing.Denied) denied = true;
            if (listing != Listing.Read) continue;

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                // "Changed" means written OR created recently: a file unpacked a minute ago keeps an old write date.
                if (useAge && entry.Changed > cutoff) continue;

                if (entry.IsDirectory && !entry.IsReparsePoint)
                {
                    var m = Measure(entry.FullPath, ct);
                    if (!m.Complete && !Directory.Exists(entry.FullPath)) continue; // removed meanwhile
                    if (useAge && (m.NewestChange > cutoff || !m.Complete))
                    {
                        // Something recent inside, or a part that could not be read (so its age is unknown):
                        // take only the old files that can be seen and leave the rest alone.
                        if (AddOldFiles(entry.FullPath, cutoff, items, ref size, ref files, ct).Denied) denied = true;
                        continue;
                    }
                    if (!m.Complete && m.Denied)
                    {
                        // No permission to read all of it, so "restart as administrator" is offered.
                        denied = true;
                        // A folder we cannot read at all would only fail on delete and clutter the result: leave it out.
                        if (m.Size == 0 && m.Files == 0) continue;
                    }
                    items.Add(entry.FullPath);
                    size += m.Size;
                    files += m.Files;
                }
                else
                {
                    // A file, or a link: deleting a link removes only the link, never what it points to.
                    items.Add(entry.FullPath);
                    if (!entry.IsDirectory)
                    {
                        size += entry.Length;
                        files++;
                    }
                }
            }
        }

        if (items.Count == 0 && !denied) return null;
        return new CleanupCandidate { Rule = rule, Roots = roots, Items = items, Size = size, FileCount = files, AccessDenied = denied };
    }

    private static CleanupCandidate? EvaluateFiles(CleanupRule rule, DateTime now, CancellationToken ct)
    {
        var roots = RootsOf(rule);
        if (roots.Count == 0) return null;

        var pattern = string.IsNullOrWhiteSpace(rule.Pattern) ? "*" : rule.Pattern;
        var useAge = rule.MinAgeDays > 0;
        var cutoff = now.AddDays(-rule.MinAgeDays);
        var items = new List<string>();
        long size = 0;
        var denied = false;

        foreach (var root in roots)
        {
            if (!SafetyGuard.IsAllowedCleanupRoot(root)) continue;

            var matches = new List<Entry>();
            try
            {
                using var walk = new FolderWalk(root, recurse: true);
                while (walk.MoveNext())
                {
                    ct.ThrowIfCancellationRequested();
                    var e = walk.Current;
                    if (!e.IsDirectory && FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(e.FullPath))) matches.Add(e);
                }
                // A folder that could not be read for lack of permission: offer "restart as administrator". A folder
                // that vanished, or a dangling junction root, is not a permission problem and gets no admin hint.
                if (walk.Denied && Directory.Exists(root)) denied = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Directory.Exists(root)) denied = true;
                continue;
            }

            // keepNewest applies to each folder on its own; save1.cok and save1.cok.cid belong together.
            foreach (var folder in matches.GroupBy(f => Path.GetDirectoryName(f.FullPath) ?? "", StringComparer.OrdinalIgnoreCase))
            {
                var groups = folder
                    .GroupBy(f => StemOf(Path.GetFileName(f.FullPath)), StringComparer.OrdinalIgnoreCase)
                    .Select(g => (Files: g.ToList(), Newest: g.Max(f => f.Written), Changed: g.Max(f => f.Changed)))
                    .OrderByDescending(g => g.Newest)
                    .Skip(Math.Max(0, rule.KeepNewest));

                foreach (var g in groups)
                {
                    ct.ThrowIfCancellationRequested();
                    if (useAge && g.Changed > cutoff) continue;
                    foreach (var f in g.Files)
                    {
                        items.Add(f.FullPath);
                        size += f.Length;
                    }
                }
            }
        }

        if (items.Count == 0 && !denied) return null;
        return new CleanupCandidate { Rule = rule, Roots = roots, Items = items, Size = size, FileCount = items.Count, AccessDenied = denied };
    }

    internal static string StemOf(string fileName)
    {
        var dot = fileName.IndexOf('.');
        return dot <= 0 ? fileName : fileName[..dot];
    }

    // ------------------------------------------------------------------------------------------------ file walking

    internal readonly record struct Entry(string FullPath, bool IsDirectory, bool IsReparsePoint, long Length, DateTime Written, DateTime Changed);

    /// <summary>
    /// Lists a folder, recursively if asked, including hidden and system files, and never walking into junctions,
    /// symbolic links, mount points or cloud placeholders: what they point to is not ours to measure or delete.
    /// A folder that cannot be read is skipped, and <see cref="Incomplete"/> says so instead of it looking empty.
    /// </summary>
    internal sealed class FolderWalk : FileSystemEnumerator<Entry>
    {
        private const int ErrorAccessDenied = 5;

        public FolderWalk(string folder, bool recurse) : base(folder, new EnumerationOptions
        {
            RecurseSubdirectories = recurse,
            IgnoreInaccessible = false, // access errors must reach ContinueOnError, which notes them
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        })
        {
        }

        /// <summary>True when the folder itself, or a folder inside it, could not be read.</summary>
        public bool Incomplete { get; private set; }

        /// <summary>True when that was for lack of permission.</summary>
        public bool Denied { get; private set; }

        protected override Entry TransformEntry(ref FileSystemEntry entry)
        {
            var written = entry.LastWriteTimeUtc.UtcDateTime;
            var created = entry.CreationTimeUtc.UtcDateTime;
            return new Entry(entry.ToFullPath(), entry.IsDirectory, (entry.Attributes & FileAttributes.ReparsePoint) != 0,
                entry.Length, written, written > created ? written : created);
        }

        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry) =>
            (entry.Attributes & FileAttributes.ReparsePoint) == 0;

        // Also called from the base constructor when the folder itself cannot be opened.
        protected override bool ContinueOnError(int error)
        {
            Incomplete = true;
            if (error == ErrorAccessDenied) Denied = true;
            return true;
        }
    }

    private enum Listing
    {
        Read,
        Denied,
        Gone,
    }

    /// <summary>Lists the top level of a folder.</summary>
    private static Listing List(string folder, out List<Entry> entries)
    {
        entries = new List<Entry>();
        try
        {
            using var walk = new FolderWalk(folder, recurse: false);
            while (walk.MoveNext()) entries.Add(walk.Current);
            if (!walk.Incomplete) return Listing.Read;
            entries.Clear();
            return walk.Denied ? Listing.Denied : Listing.Gone;
        }
        catch (UnauthorizedAccessException)
        {
            entries.Clear();
            return Listing.Denied;
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException)
        {
            entries.Clear(); // the folder vanished meanwhile: nothing to clean
            return Listing.Gone;
        }
    }

    /// <param name="Complete">False if part of the folder could not be read, so the numbers may be too low.</param>
    /// <param name="Denied">True when a part could not be read for lack of permission (as opposed to vanishing).</param>
    internal readonly record struct Measurement(long Size, long Files, DateTime NewestChange, bool Complete, bool Denied);

    internal static Measurement Measure(string folder, CancellationToken ct)
    {
        long size = 0, files = 0;
        var newest = DateTime.MinValue;
        var complete = false;
        var denied = false;
        try
        {
            using var walk = new FolderWalk(folder, recurse: true);
            while (walk.MoveNext())
            {
                ct.ThrowIfCancellationRequested();
                var e = walk.Current;
                if (e.Changed > newest) newest = e.Changed;
                if (e.IsDirectory) continue;
                size += e.Length;
                files++;
            }
            complete = !walk.Incomplete;
            denied = walk.Denied;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return new Measurement(size, files, newest, complete, denied);
    }

    /// <summary>Adds the files under <paramref name="folder"/> that did not change after <paramref name="cutoff"/>.</summary>
    /// <returns>Whether the whole folder was read, and (if not) whether that was for lack of permission.</returns>
    private static (bool Complete, bool Denied) AddOldFiles(string folder, DateTime cutoff, List<string> items,
        ref long size, ref long files, CancellationToken ct)
    {
        try
        {
            using var walk = new FolderWalk(folder, recurse: true);
            while (walk.MoveNext())
            {
                ct.ThrowIfCancellationRequested();
                var f = walk.Current;
                if (f.IsDirectory || f.Changed > cutoff) continue;
                items.Add(f.FullPath);
                size += f.Length;
                files++;
            }
            if (!walk.Incomplete || !Directory.Exists(folder)) return (true, false);
            return (false, walk.Denied);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (!Directory.Exists(folder), false);
        }
    }
}
