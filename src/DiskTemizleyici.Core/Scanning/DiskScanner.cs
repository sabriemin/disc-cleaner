using System.IO.Enumeration;

namespace DiskTemizleyici.Core.Scanning;

public sealed class ScanResult
{
    public required DirNode Root { get; init; }
    public required List<LargeFile> LargestFiles { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required long DirectoriesScanned { get; init; }
    public required long InaccessibleDirectories { get; init; }

    /// <summary>Size per file extension, largest first (files that are on this disk only).</summary>
    public IReadOnlyList<FileTypeStat> FileTypes { get; init; } = Array.Empty<FileTypeStat>();
}

/// <summary>
/// Walks a folder tree and adds up sizes.
/// <list type="bullet">
/// <item>Junctions and symbolic links are not followed, so nothing is counted twice
/// (e.g. "Application Data" inside the user profile).</item>
/// <item>"Online-only" cloud placeholder files are counted separately, because they do not use disk space.</item>
/// </list>
/// </summary>
public sealed class DiskScanner
{
    // File attributes that mark a cloud placeholder whose data is not on this disk.
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes CloudOnlyMask = RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline;

    private const int ParallelDepth = 8;

    private long _files;
    private long _dirs;
    private long _bytes;
    private long _denied;
    private string _currentPath = "";

    // One table per worker thread, merged when the scan ends, so counting extensions needs no lock.
    private sealed class TypeTotals
    {
        public long Bytes;
        public long Count;
    }

    private ThreadLocal<Dictionary<string, TypeTotals>>? _types;

    public int LargeFileCount { get; init; } = 200;

    public long FilesScanned => Interlocked.Read(ref _files);
    public long DirectoriesScanned => Interlocked.Read(ref _dirs);
    public long BytesScanned => Interlocked.Read(ref _bytes);
    public string CurrentPath => Volatile.Read(ref _currentPath);

    public ScanResult Scan(string rootPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(rootPath)) throw new DirectoryNotFoundException(rootPath);

        _files = _dirs = _bytes = _denied = 0;
        var started = DateTime.UtcNow;
        var tracker = new LargeFileTracker(LargeFileCount);
        var root = new DirNode(Path.GetFullPath(rootPath), null);

        using var types = new ThreadLocal<Dictionary<string, TypeTotals>>(
            () => new Dictionary<string, TypeTotals>(StringComparer.OrdinalIgnoreCase), trackAllValues: true);
        _types = types;
        try
        {
            ScanDirectory(root, 0, tracker, ct);
        }
        finally
        {
            _types = null;
        }

        return new ScanResult
        {
            Root = root,
            LargestFiles = tracker.ToSortedList(),
            Elapsed = DateTime.UtcNow - started,
            DirectoriesScanned = DirectoriesScanned,
            InaccessibleDirectories = Interlocked.Read(ref _denied),
            FileTypes = MergeTypes(types),
        };
    }

    private static List<FileTypeStat> MergeTypes(ThreadLocal<Dictionary<string, TypeTotals>> types)
    {
        var merged = new Dictionary<string, (long Bytes, long Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in types.Values)
        {
            foreach (var (ext, t) in table)
            {
                merged.TryGetValue(ext, out var sum);
                merged[ext] = (sum.Bytes + t.Bytes, sum.Count + t.Count);
            }
        }
        return merged
            .Select(kv => new FileTypeStat(kv.Key.ToLowerInvariant(), FileCategories.Of(kv.Key), kv.Value.Bytes, kv.Value.Count))
            .OrderByDescending(s => s.Bytes)
            .ToList();
    }

    private readonly record struct Entry(string Name, bool IsDirectory, long Length, FileAttributes Attributes, DateTime LastWrite);

    // IgnoreInaccessible must stay false: with true, .NET silently returns "no entries" for a folder it may not
    // read, and such folders would look empty instead of being marked as unreadable.
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
    };

    /// <summary>
    /// Scans one folder again (after a delete, or when the user asks) into a new node that has the same parent.
    /// Put it into the tree with <see cref="DirNode.ReplaceChild"/> on the thread that owns the tree.
    /// </summary>
    public DirNode ScanDetached(DirNode node, CancellationToken ct = default)
    {
        var fresh = new DirNode(node.FullPath, node.Parent) { IsLink = node.IsLink };
        if (node.IsLink || !Directory.Exists(node.FullPath)) return fresh;
        ScanDirectory(fresh, 1, new LargeFileTracker(1), ct);
        return fresh;
    }

    private void ScanDirectory(DirNode node, int depth, LargeFileTracker tracker, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _dirs);
        Volatile.Write(ref _currentPath, node.FullPath);

        List<Entry> entries;
        try
        {
            var enumerable = new FileSystemEnumerable<Entry>(
                node.FullPath,
                (ref FileSystemEntry e) => new Entry(
                    e.FileName.ToString(), e.IsDirectory, e.Length, e.Attributes,
                    e.LastWriteTimeUtc.UtcDateTime),
                Options);
            entries = enumerable.ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            node.AccessDenied = true;
            Interlocked.Increment(ref _denied);
            return;
        }

        long ownSize = 0, ownFiles = 0, cloudSize = 0, cloudFiles = 0;
        var subDirs = new List<DirNode>();
        var typeTable = _types?.Value;

        foreach (var e in entries)
        {
            var full = Path.Join(node.FullPath, e.Name);
            if (e.IsDirectory)
            {
                var child = new DirNode(full, node);
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0 && IsLink(full))
                {
                    child.IsLink = true; // junction / symlink: do not follow, would double count
                    node.AddChild(child);
                    continue;
                }
                node.AddChild(child);
                subDirs.Add(child);
            }
            else if ((e.Attributes & CloudOnlyMask) != 0)
            {
                cloudSize += e.Length;
                cloudFiles++;
            }
            else
            {
                ownSize += e.Length;
                ownFiles++;
                tracker.Offer(full, e.Length, e.LastWrite);
                if (typeTable is not null)
                {
                    var ext = FileCategories.ExtensionOf(e.Name);
                    if (!typeTable.TryGetValue(ext, out var t)) typeTable[ext] = t = new TypeTotals();
                    t.Bytes += e.Length;
                    t.Count++;
                }
            }
        }

        Interlocked.Add(ref _files, ownFiles + cloudFiles);
        Interlocked.Add(ref _bytes, ownSize);

        if (depth < ParallelDepth && subDirs.Count > 1)
        {
            Parallel.ForEach(subDirs, new ParallelOptions { CancellationToken = ct },
                child => ScanDirectory(child, depth + 1, tracker, ct));
        }
        else
        {
            foreach (var child in subDirs) ScanDirectory(child, depth + 1, tracker, ct);
        }

        long size = ownSize, files = ownFiles, cSize = cloudSize, cFiles = cloudFiles;
        foreach (var child in subDirs)
        {
            size += child.Size;
            files += child.FileCount;
            cSize += child.CloudOnlySize;
            cFiles += child.CloudOnlyCount;
        }

        node.OwnFilesSize = ownSize;
        node.Size = size;
        node.FileCount = files;
        node.CloudOnlySize = cSize;
        node.CloudOnlyCount = cFiles;
        node.SortChildren();
    }

    /// <summary>
    /// A reparse point is a "link" (junction, symlink, mount point) when it has a link target.
    /// Cloud-sync folders (OneDrive, CrossDevice) are reparse points too, but have no link target,
    /// so they are scanned normally.
    /// </summary>
    private static bool IsLink(string path)
    {
        try
        {
            return new DirectoryInfo(path).LinkTarget is not null;
        }
        catch
        {
            return true; // unknown reparse point: safer not to follow
        }
    }
}
