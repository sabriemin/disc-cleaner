using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskTemizleyici.Core.Cleanup;

public enum DeleteMode
{
    /// <summary>Move to the Recycle Bin (can be restored).</summary>
    RecycleBin,

    /// <summary>Delete permanently (frees the space at once, cannot be undone).</summary>
    Permanent,
}

public enum DeleteProblemKind
{
    /// <summary>The safety guard refused the path; see <see cref="DeleteProblem.BlockReason"/>.</summary>
    Blocked,

    /// <summary>Another program has the file open.</summary>
    InUse,

    /// <summary>No permission (often needs "Run as administrator").</summary>
    AccessDenied,

    /// <summary>The shell did not move the item to the Recycle Bin.</summary>
    NotMovedToRecycleBin,

    /// <summary>Something inside changed after the suggestion was worked out, so it was left alone.</summary>
    ChangedRecently,

    /// <summary>Anything else; <see cref="DeleteProblem.Detail"/> has the system message.</summary>
    Other,
}

public sealed record DeleteProblem(string Path, DeleteProblemKind Kind, BlockReason BlockReason = BlockReason.None, string? Detail = null);

/// <summary>Extra checks for deletes that come from a cleanup rule rather than from an explicit click.</summary>
/// <param name="ConfineTo">
/// The rule's folders. Every item must still lie inside one of them, with no junction or symbolic link on the way,
/// when it is deleted. That stops a folder from being swapped for a link to somewhere else after the check.
/// </param>
/// <param name="SkipChangedAfterUtc">Leave items alone if they (or anything inside them) changed after this time.</param>
public sealed record DeleteOptions(IReadOnlyCollection<string>? ConfineTo = null, DateTime? SkipChangedAfterUtc = null);

public sealed class DeleteReport
{
    private const int MaxProblems = 1000;

    /// <summary>Items that existed and are gone now.</summary>
    public long ItemsDeleted { get; internal set; }

    /// <summary>Items that were already gone before the delete started.</summary>
    public long ItemsMissing { get; internal set; }

    /// <summary>Items that could not be removed (completely).</summary>
    public long ItemsFailed { get; internal set; }

    /// <summary>Items the safety guard refused.</summary>
    public long ItemsBlocked { get; internal set; }

    /// <summary>Items left alone because they changed after the suggestion was worked out.</summary>
    public long ItemsChanged { get; internal set; }

    /// <summary>True if the delete was stopped (by the app or by "No" in a Windows prompt) before all items were handled.</summary>
    public bool Cancelled { get; internal set; }

    /// <summary>What went wrong, at most 1000 entries; folders can add one entry per file that stayed.</summary>
    public List<DeleteProblem> Problems { get; } = new();

    internal void Add(DeleteProblem problem)
    {
        if (Problems.Count < MaxProblems) Problems.Add(problem);
    }
}

public static class Deleter
{
    private const int RecycleBatchSize = 200;

    /// <summary>
    /// Deletes the given files and folders. Each path is checked with <see cref="SafetyGuard"/> first, and again
    /// right before it is deleted. Files that are in use or not permitted are skipped and listed in the report.
    /// Links (junctions, symbolic links) are removed themselves; what they point to is never touched.
    /// Cancelling stops between items and returns what was done so far (<see cref="DeleteReport.Cancelled"/>).
    /// </summary>
    public static DeleteReport Delete(IReadOnlyList<string> paths, DeleteMode mode, IProgress<int>? progress = null,
        CancellationToken ct = default, IntPtr ownerWindow = default, DeleteOptions? options = null)
    {
        var report = new DeleteReport();
        var checker = new SafetyGuard.PathChecker();
        var allowed = new List<string>();
        foreach (var p in paths)
        {
            if (!Exists(p))
            {
                report.ItemsMissing++;
                continue;
            }
            if (checker.CanDelete(p, out var reason))
            {
                allowed.Add(p);
            }
            else
            {
                report.ItemsBlocked++;
                report.Add(new DeleteProblem(p, DeleteProblemKind.Blocked, reason));
            }
        }

        var done = 0;
        if (mode == DeleteMode.RecycleBin)
        {
            foreach (var chunk in allowed.Chunk(RecycleBatchSize))
            {
                if (ct.IsCancellationRequested)
                {
                    report.Cancelled = true;
                    break;
                }
                var batch = chunk.Where(p => LastMomentCheck(p, options, report)).ToArray();
                var batchCode = batch.Length == 0 ? 0 : RecycleBin.Send(batch, ownerWindow);
                if (batchCode == -1)
                {
                    // "No" to Windows' own "delete permanently?" prompt: stop here, ask nothing more.
                    report.Cancelled = true;
                    CountAfterSend(batch, report, retry: null);
                    break;
                }
                CountAfterSend(batch, report, retry: p => ct.IsCancellationRequested ? 0 : RecycleBin.Send(new[] { p }, ownerWindow));
                if (report.Cancelled) break;
                done += chunk.Length;
                progress?.Report(done);
            }
            return report;
        }

        foreach (var p in allowed)
        {
            if (ct.IsCancellationRequested)
            {
                report.Cancelled = true;
                break;
            }
            if (!LastMomentCheck(p, options, report))
            {
                progress?.Report(++done);
                continue;
            }
            try
            {
                // A rule-driven delete is confined to the rule's folders, so each directory is pinned open while it is
                // emptied: that stops a folder from being swapped for a junction mid-delete and the delete following it out.
                if (Directory.Exists(p)) DeleteDirectory(p, report, pin: options?.ConfineTo is { Count: > 0 });
                else DeleteFile(p);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Add(new DeleteProblem(p, Classify(ex), Detail: ex.Message));
            }

            if (Exists(p)) report.ItemsFailed++;
            else report.ItemsDeleted++;
            progress?.Report(++done);
        }
        return report;
    }

    /// <summary>
    /// After the shell call: counts what is gone. Items still there are retried one by one (one locked file can make
    /// the shell give up on the rest of a batch), unless the user said "No" to a Windows prompt.
    /// </summary>
    private static void CountAfterSend(string[] batch, DeleteReport report, Func<string, int>? retry)
    {
        foreach (var p in batch)
        {
            if (!Exists(p))
            {
                report.ItemsDeleted++;
                continue;
            }
            var code = 0;
            if (retry is not null && !report.Cancelled)
            {
                code = retry(p);
                if (code == -1) report.Cancelled = true; // the user refused again: no more prompts
            }
            if (Exists(p))
            {
                report.ItemsFailed++;
                report.Add(new DeleteProblem(p, DeleteProblemKind.NotMovedToRecycleBin, Detail: code is not 0 and not -1 ? $"0x{code:X}" : null));
            }
            else
            {
                report.ItemsDeleted++;
            }
        }
    }

    /// <summary>Checks done right before an item is touched, so changes since the first check are caught.</summary>
    private static bool LastMomentCheck(string path, DeleteOptions? options, DeleteReport report)
    {
        if (!Exists(path))
        {
            report.ItemsMissing++; // removed by something else in the meantime
            return false;
        }
        if (options is null) return true;
        if (options.ConfineTo is { Count: > 0 } roots && !IsInsideWithoutLinks(path, roots))
        {
            report.ItemsBlocked++;
            report.Add(new DeleteProblem(path, DeleteProblemKind.Blocked, BlockReason.RedirectedPath));
            return false;
        }
        if (options.SkipChangedAfterUtc is { } cutoff && ChangedAfter(path, cutoff))
        {
            report.ItemsChanged++;
            report.Add(new DeleteProblem(path, DeleteProblemKind.ChangedRecently));
            return false;
        }
        return true;
    }

    /// <summary>
    /// True if <paramref name="path"/> lies inside one of <paramref name="roots"/> and no folder between the root and
    /// the item is a junction or symbolic link (the item itself may be one: deleting it removes only the link).
    /// </summary>
    internal static bool IsInsideWithoutLinks(string path, IReadOnlyCollection<string> roots)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (var root in roots)
        {
            string fullRoot;
            try
            {
                fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;

            for (var dir = Path.GetDirectoryName(full); dir is not null && dir.Length > fullRoot.Length; dir = Path.GetDirectoryName(dir))
            {
                try
                {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return false;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            }
            return true;
        }
        return false;
    }

    /// <summary>True if the file, or anything in the folder, was written or created after <paramref name="cutoffUtc"/>.</summary>
    internal static bool ChangedAfter(string path, DateTime cutoffUtc)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            var isFolder = (attrs & FileAttributes.Directory) != 0;
            FileSystemInfo info = isFolder ? new DirectoryInfo(path) : new FileInfo(path);
            if (info.LastWriteTimeUtc > cutoffUtc || info.CreationTimeUtc > cutoffUtc) return true;
            // a file, or a link: only the entry itself is deleted, so only its own dates count
            if (!isFolder || (attrs & FileAttributes.ReparsePoint) != 0) return false;

            using var walk = new RuleEvaluator.FolderWalk(path, recurse: true);
            while (walk.MoveNext())
            {
                if (walk.Current.Changed > cutoffUtc) return true;
            }
            // a part that could not be read counts as "changed": better to keep something than to guess
            return walk.Incomplete;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    internal static DeleteProblemKind Classify(Exception ex) => ex switch
    {
        UnauthorizedAccessException => DeleteProblemKind.AccessDenied,
        IOException io when (io.HResult & 0xFFFF) is 32 or 33 => DeleteProblemKind.InUse, // sharing / lock violation
        _ => DeleteProblemKind.Other,
    };

    private static void DeleteFile(string path)
    {
        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    private static readonly EnumerationOptions OneLevelEverything = new()
    {
        AttributesToSkip = 0, // hidden and system files too, or the folder can never become empty
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Deletes a folder bottom-up, skipping what cannot be deleted instead of stopping. When <paramref name="pin"/> is
    /// set (a rule-driven delete), the folder is held open with a no-share-delete handle while it is emptied, so it
    /// cannot be renamed away and replaced by a junction between the reparse-point check and the enumerate/delete that
    /// follows; a directory that is already a link when opened is unlinked without being entered.
    /// </summary>
    private static void DeleteDirectory(string dir, DeleteReport report, bool pin)
    {
        if (pin && TryOpenPinnedDirectory(dir, out var handle, out var isReparse))
        {
            try
            {
                if (isReparse)
                {
                    // A junction, symbolic link or mount point: must not be entered. Release the pin, then unlink only.
                    handle.Dispose();
                    Directory.Delete(dir, recursive: false);
                    return;
                }
                DeleteDirectoryContents(dir, report, pin); // the handle keeps "dir" from being swapped meanwhile
            }
            finally
            {
                handle.Dispose();
            }
            RemoveEmptyDirectory(dir); // empty now; a last-moment swap to a link is unlinked here, not followed
            return;
        }

        var attrs = File.GetAttributes(dir);
        if ((attrs & FileAttributes.ReparsePoint) != 0)
        {
            // A junction, symbolic link, mount point or cloud placeholder: remove only the entry itself.
            // For a placeholder folder that still has content this fails, which is what we want.
            Directory.Delete(dir, recursive: false);
            return;
        }
        DeleteDirectoryContents(dir, report, pin);
        RemoveEmptyDirectory(dir);
    }

    /// <summary>Deletes the files and sub-folders directly inside <paramref name="dir"/>; the folder itself stays.</summary>
    private static void DeleteDirectoryContents(string dir, DeleteReport report, bool pin)
    {
        try
        {
            var attrs = File.GetAttributes(dir);
            if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(dir, attrs & ~FileAttributes.ReadOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        foreach (var file in SafeList(() => Directory.EnumerateFiles(dir, "*", OneLevelEverything)))
        {
            try
            {
                DeleteFile(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Add(new DeleteProblem(file, Classify(ex), Detail: ex.Message));
            }
        }
        foreach (var sub in SafeList(() => Directory.EnumerateDirectories(dir, "*", OneLevelEverything)))
        {
            try
            {
                DeleteDirectory(sub, report, pin); // checks again whether "sub" is a link before entering it
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Add(new DeleteProblem(sub, Classify(ex), Detail: ex.Message));
            }
        }
    }

    private static void RemoveEmptyDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: false);
        }
        catch (IOException) when (Directory.EnumerateFileSystemEntries(dir, "*", OneLevelEverything).Any())
        {
            // not empty because something inside was in use: already reported above
        }
    }

    /// <summary>
    /// Opens a directory with a handle that forbids delete sharing, so it cannot be renamed or deleted (and thus swapped
    /// for a link) while held. The reparse point itself is opened, not its target, so <paramref name="isReparse"/> tells
    /// a real directory from a junction. Returns false if it could not be opened or its kind could not be read.
    /// </summary>
    private static bool TryOpenPinnedDirectory(string dir, out SafeFileHandle handle, out bool isReparse)
    {
        isReparse = false;
        // Read-data (list-directory) access, NOT attributes-only: a FILE_READ_ATTRIBUTES handle is skipped by Windows
        // share-access enforcement, so the share mode would be ignored and the pin would do nothing. With list-directory
        // access and a share mode of read only (no write, no delete), no other opener can rename it, delete it, or set a
        // reparse point on it (FSCTL_SET_REPARSE_POINT needs write) while the handle is held, so it cannot be turned into
        // a junction mid-delete.
        handle = CreateFileW(dir, FileListDirectory, FileShareRead, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return false;
        }
        if (!GetFileInformationByHandle(handle, out var info))
        {
            handle.Dispose();
            return false;
        }
        isReparse = (info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0;
        return true;
    }

    private const uint OpenExisting = 3;
    private const uint FileListDirectory = 0x00000001; // read-data on a directory: makes the share mode take effect
    private const uint FileShareRead = 0x00000001;     // others may read, but not write, rename, delete or reparse it
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private static List<string> SafeList(Func<IEnumerable<string>> list)
    {
        try
        {
            return list().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }
}
