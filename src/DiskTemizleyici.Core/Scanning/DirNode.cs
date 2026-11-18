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

    /// <summary>Set when this node was replaced by a fresh scan or removed; it is no longer part of the tree.</summary>
    public bool IsDetached { get; private set; }

    /// <summary>True while this node and all of its ancestors are still part of the tree.</summary>
    public bool IsAttached
    {
        get
        {
            for (var n = this; n is not null; n = n.Parent)
            {
                if (n.IsDetached) return false;
            }
            return true;
        }
    }

    internal void AddChild(DirNode child) => _children.Add(child);

    internal void SortChildren()
    {
        _children.Sort((a, b) => b.Size.CompareTo(a.Size));
    }

    /// <summary>
    /// Removes a sub-folder (after it was deleted) and subtracts its size from every ancestor.
    /// Returns false, changing nothing, if the child is not here or this part of the tree was already replaced.
    /// </summary>
    public bool RemoveChild(DirNode child) => ReplaceChild(child, null);

    /// <summary>
    /// Puts a freshly scanned copy of a sub-folder in place of the old one (or removes it when <paramref name="fresh"/>
    /// is null), passes the size change up to every ancestor and keeps every level sorted, largest first.
    /// Returns false, changing nothing, if <paramref name="old"/> is not a child here or this node is no longer
    /// in the tree (a refresh that finished late must not change the sizes of the live ancestors a second time).
    /// </summary>
    public bool ReplaceChild(DirNode old, DirNode? fresh)
    {
        if (!IsAttached) return false;
        var index = _children.IndexOf(old);
        if (index < 0) return false;
        if (fresh is not null && fresh.Parent != this) throw new ArgumentException("The new node must have the same parent.", nameof(fresh));

        var dSize = (fresh?.Size ?? 0) - old.Size;
        var dFiles = (fresh?.FileCount ?? 0) - old.FileCount;
        var dCloud = (fresh?.CloudOnlySize ?? 0) - old.CloudOnlySize;
        var dCloudFiles = (fresh?.CloudOnlyCount ?? 0) - old.CloudOnlyCount;

        if (fresh is null) _children.RemoveAt(index);
        else _children[index] = fresh;
        old.IsDetached = true;

        for (var n = this; n is not null; n = n.Parent)
        {
            n.Size += dSize;
            n.FileCount += dFiles;
            n.CloudOnlySize += dCloud;
            n.CloudOnlyCount += dCloudFiles;
            n.SortChildren();
        }
        return true;
    }

    /// <summary>
    /// Takes a deleted file that lay directly in this folder out of the totals here and in every ancestor.
    /// Returns false, changing nothing, if this node is no longer in the tree.
    /// </summary>
    public bool RemoveOwnFile(long size)
    {
        if (!IsAttached || FileCount <= 0) return false;
        size = Math.Clamp(size, 0, OwnFilesSize);
        OwnFilesSize -= size;
        for (var n = this; n is not null; n = n.Parent)
        {
            n.Size -= size;
            n.FileCount--;
            n.SortChildren();
        }
        return true;
    }

    /// <summary>
    /// Adds a freshly scanned sub-folder that was not in the tree before (e.g. a <c>$Recycle.Bin</c> that only appeared
    /// after something was recycled), and passes its size up to every ancestor. Returns false, changing nothing, if this
    /// node is no longer in the tree or a child with that path is already here.
    /// </summary>
    public bool AddScannedChild(DirNode fresh)
    {
        if (!IsAttached) return false;
        if (fresh.Parent != this) throw new ArgumentException("The new node must have this node as its parent.", nameof(fresh));
        var target = Path.TrimEndingDirectorySeparator(fresh.FullPath);
        if (_children.Any(c => string.Equals(Path.TrimEndingDirectorySeparator(c.FullPath), target, StringComparison.OrdinalIgnoreCase)))
            return false;

        _children.Add(fresh);
        for (var n = this; n is not null; n = n.Parent)
        {
            n.Size += fresh.Size;
            n.FileCount += fresh.FileCount;
            n.CloudOnlySize += fresh.CloudOnlySize;
            n.CloudOnlyCount += fresh.CloudOnlyCount;
            n.SortChildren();
        }
        return true;
    }

    /// <summary>Marks a whole tree (a root node) as replaced, e.g. by a new scan, so late refreshes leave it alone.</summary>
    public void Detach() => IsDetached = true;

    /// <summary>True if this node is <paramref name="other"/> or one of its ancestors.</summary>
    public bool IsAncestorOrSelf(DirNode other)
    {
        for (var n = other; n is not null; n = n.Parent)
        {
            if (ReferenceEquals(n, this)) return true;
        }
        return false;
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
