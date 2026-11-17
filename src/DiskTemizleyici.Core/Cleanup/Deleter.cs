namespace DiskTemizleyici.Core.Cleanup;

public enum DeleteMode
{
    /// <summary>Move to the Recycle Bin (can be restored).</summary>
    RecycleBin,

    /// <summary>Delete permanently (frees the space at once, cannot be undone).</summary>
    Permanent,
}

public sealed class DeleteReport
{
    public long ItemsDeleted { get; internal set; }
    public long ItemsFailed { get; internal set; }
    public long ItemsBlocked { get; internal set; }
    public List<string> Errors { get; } = new();
}

public static class Deleter
{
    private const int RecycleBatchSize = 200;

    /// <summary>
    /// Deletes the given files and folders. Each path is checked with <see cref="SafetyGuard"/> first.
    /// Files that are in use or not permitted are skipped and listed in the report.
    /// </summary>
    public static DeleteReport Delete(IReadOnlyList<string> paths, DeleteMode mode, IProgress<int>? progress = null,
        CancellationToken ct = default, IntPtr ownerWindow = default)
    {
        var report = new DeleteReport();
        var allowed = new List<string>();
        foreach (var p in paths)
        {
            if (SafetyGuard.CanDelete(p, out var reason)) allowed.Add(p);
            else
            {
                report.ItemsBlocked++;
                report.Errors.Add($"{p}: blocked ({reason})");
            }
        }

        var done = 0;
        if (mode == DeleteMode.RecycleBin)
        {
            foreach (var batch in allowed.Chunk(RecycleBatchSize))
            {
                ct.ThrowIfCancellationRequested();
                RecycleBin.Send(batch, ownerWindow);
                foreach (var p in batch)
                {
                    if (File.Exists(p) || Directory.Exists(p))
                    {
                        report.ItemsFailed++;
                        report.Errors.Add($"{p}: could not be moved to the Recycle Bin");
                    }
                    else report.ItemsDeleted++;
                }
                done += batch.Length;
                progress?.Report(done);
            }
            return report;
        }

        foreach (var p in allowed)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (Directory.Exists(p)) DeleteDirectory(p, report);
                else if (File.Exists(p)) DeleteFile(p);
                if (File.Exists(p) || Directory.Exists(p)) report.ItemsFailed++;
                else report.ItemsDeleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.ItemsFailed++;
                report.Errors.Add($"{p}: {ex.Message}");
            }
            progress?.Report(++done);
        }
        return report;
    }

    private static void DeleteFile(string path)
    {
        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    /// <summary>Deletes a folder bottom-up, skipping what cannot be deleted instead of stopping.</summary>
    private static void DeleteDirectory(string dir, DeleteReport report)
    {
        var info = new DirectoryInfo(dir);
        if (info.LinkTarget is not null)
        {
            info.Delete(); // remove only the link, never what it points to
            return;
        }

        foreach (var file in SafeList(() => Directory.EnumerateFiles(dir)))
        {
            try { DeleteFile(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (report.Errors.Count < 500) report.Errors.Add($"{file}: {ex.Message}");
            }
        }
        foreach (var sub in SafeList(() => Directory.EnumerateDirectories(dir)))
        {
            DeleteDirectory(sub, report);
        }
        try { Directory.Delete(dir, false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not empty because something inside was in use: leave it
        }
    }

    private static List<string> SafeList(Func<IEnumerable<string>> list)
    {
        try { return list().ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
    }
}
