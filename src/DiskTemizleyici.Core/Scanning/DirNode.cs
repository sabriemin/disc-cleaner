namespace DiskTemizleyici.Core.Scanning;

/// <summary>
/// One folder in the scanned tree. Sizes include all sub-folders.
/// </summary>
public sealed class DirNode
{
    private readonly List<DirNode> _children = new();

    public DirNode(string fullPath, DirNode? parent)
    {
        FullPath = fullPath;
        Parent = parent;
        Name = parent is null ? fullPath : Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar));
    }

    public string Name { get; }
    public string FullPath { get; }
    public DirNode? Parent { get; }
    public IReadOnlyList<DirNode> Children => _children;

    /// <summary>Bytes that really live on this disk (cloud-only placeholders are excluded).</summary>
    public long Size { get; internal set; }

    /// <summary>Bytes of files directly inside this folder (not in sub-folders).</summary>
    public long OwnFilesSize { get; internal set; }

    public long FileCount { get; internal set; }

    /// <summary>Logical size of "online-only" files (OneDrive, Phone Link / CrossDevice...). They use no disk space.</summary>
    public long CloudOnlySize { get; internal set; }

    public long CloudOnlyCount { get; internal set; }

    /// <summary>True if the folder could not be read (permissions).</summary>
    public bool AccessDenied { get; internal set; }

    /// <summary>True if the folder is a junction / symbolic link that was not followed.</summary>
    public bool IsLink { get; internal set; }

    internal void AddChild(DirNode child) => _children.Add(child);

    internal void SortChildren()
    {
        _children.Sort((a, b) => b.Size.CompareTo(a.Size));
    }

    /// <summary>
    /// Removes a sub-folder (after it was deleted) and subtracts its size from every ancestor.
    /// </summary>
    public void RemoveChild(DirNode child)
    {
        if (!_children.Remove(child)) return;
        for (var n = this; n is not null; n = n.Parent)
        {
            n.Size -= child.Size;
            n.FileCount -= child.FileCount;
            n.CloudOnlySize -= child.CloudOnlySize;
            n.CloudOnlyCount -= child.CloudOnlyCount;
        }
    }

    /// <summary>Subtracts bytes freed somewhere below this folder from it and its ancestors.</summary>
    public void SubtractFreed(long bytes, long files)
    {
        for (var n = this; n is not null; n = n.Parent)
        {
            n.Size = Math.Max(0, n.Size - bytes);
            n.FileCount = Math.Max(0, n.FileCount - files);
        }
    }

    /// <summary>Finds the node for a path inside this tree, or null.</summary>
    public DirNode? Find(string path)
    {
        var target = Path.TrimEndingDirectorySeparator(path);
        var self = Path.TrimEndingDirectorySeparator(FullPath);
        if (string.Equals(target, self, StringComparison.OrdinalIgnoreCase)) return this;
        if (!target.StartsWith(self + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !(self.EndsWith(Path.DirectorySeparatorChar) && target.StartsWith(self, StringComparison.OrdinalIgnoreCase)))
            return null;
        foreach (var c in _children)
        {
            var hit = c.Find(target);
            if (hit is not null) return hit;
        }
        return null;
    }

    public override string ToString() => $"{FullPath} ({Size} B)";
}
