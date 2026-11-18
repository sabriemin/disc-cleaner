using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskTemizleyici.App.Localization;
using DiskTemizleyici.Core;
using DiskTemizleyici.Core.Scanning;

namespace DiskTemizleyici.App.ViewModels;

/// <summary>One row of the folder tree. Children are created only when the row is expanded.</summary>
public sealed partial class FolderItemViewModel : ObservableObject
{
    private static readonly FolderItemViewModel Placeholder = new();
    private bool _loaded;

    private FolderItemViewModel()
    {
        Name = "…";
    }

    public FolderItemViewModel(DirNode node, FolderItemViewModel? parent)
    {
        Node = node;
        Parent = parent;
        Name = node.Name;
        if (node.Children.Count > 0) Children.Add(Placeholder);
    }

    /// <summary>Row standing for "files directly in this folder".</summary>
    private FolderItemViewModel(FolderItemViewModel owner)
    {
        Parent = owner;
        IsFilesRow = true;
        Name = Loc.I["FilesHere"];
    }

    /// <summary>Row for space the drive reports as used but the scan did not find.</summary>
    private FolderItemViewModel(FolderItemViewModel owner, long unaccounted)
    {
        Parent = owner;
        IsUnaccountedRow = true;
        _unaccounted = unaccounted;
        Name = Loc.I["UnaccountedRow"];
    }

    private long _unaccounted;
    private long _unaccountedSize;

    /// <summary>The folder; replaced by a freshly scanned copy after a rescan (see <see cref="Rebind"/>).</summary>
    public DirNode? Node { get; private set; }

    public FolderItemViewModel? Parent { get; }
    public bool IsFilesRow { get; }
    public bool IsUnaccountedRow { get; }

    /// <summary>A row that stands for something other than a real folder.</summary>
    public bool IsSpecialRow => IsFilesRow || IsUnaccountedRow;

    /// <summary>Bound two-way to the tree, so the view model can clear a selection the tree made by itself.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>On the root row of a drive scan: used space that the scan did not find (0 = hide the row).</summary>
    public long UnaccountedSize
    {
        get => _unaccountedSize;
        set
        {
            if (_unaccountedSize == value) return;
            _unaccountedSize = value;
            if (_loaded) Rebuild();
        }
    }

    /// <summary>Changes the size this "unaccounted space" row stands for, in place, and tells the view.</summary>
    private void SetUnaccounted(long value)
    {
        if (_unaccounted == value) return;
        _unaccounted = value;
        OnPropertyChanged(nameof(Size));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(Share));
        OnPropertyChanged(nameof(ShareText));
    }

    /// <summary>Tooltip: what the row is, or where the folder is.</summary>
    public string RowTip =>
        IsUnaccountedRow ? Loc.I["UnaccountedTip"]
        : Node is { CloudOnlySize: > 0 } && !Node.IsLink && !Node.AccessDenied ? Node.FullPath + "\n\n" + Loc.I["CloudOnlyTip"]
        : FolderNode?.FullPath ?? "";

    public override string ToString() => string.IsNullOrEmpty(Note) ? $"{Name}, {SizeText}" : $"{Name}, {SizeText}, {Note}";
    public string Name { get; private set; }
    public ObservableCollection<FolderItemViewModel> Children { get; } = new();

    public int Depth => Parent is null ? 0 : Parent.Depth + 1;

    /// <summary>
    /// Rows are full width and indent only their chevron, so the size columns line up for every depth.
    /// </summary>
    public System.Windows.Thickness IndentMargin => new(Math.Min(Depth, 14) * IndentPerLevel, 0, 0, 0);

    public const double IndentPerLevel = 18;

    /// <summary>Segoe Fluent / MDL2 glyph for the row's icon tile.</summary>
    public string Glyph =>
        IsUnaccountedRow ? "" :    // Unknown
        IsFilesRow ? "" :          // Document
        Node is null ? "" :
        Node.IsLink ? "" :          // Link
        Node.AccessDenied ? "" :    // Lock
        Node.Parent is null ? "" :  // HardDrive
        "";                         // Folder

    public bool HasNote => !string.IsNullOrEmpty(Note);

    /// <summary>The folder that this row stands for (the parent's folder for the "files in this folder" row).</summary>
    public DirNode? FolderNode => IsUnaccountedRow ? null : IsFilesRow ? Parent?.Node : Node;

    public long Size => IsUnaccountedRow ? _unaccounted : IsFilesRow ? Parent?.Node?.OwnFilesSize ?? 0 : Node?.Size ?? 0;

    public string SizeText => ByteFormat.Format(Size, Loc.I.Culture);

    /// <summary>Share of the parent folder, 0–100.</summary>
    public double Share
    {
        get
        {
            var parentSize = IsSpecialRow ? Parent?.Node?.Size : Node?.Parent?.Size;
            return parentSize is > 0 ? Math.Min(100.0, 100.0 * Size / parentSize.Value) : 100.0;
        }
    }

    public string ShareText => Share.ToString("0.0", Loc.I.Culture) + " %";

    public string FilesText => IsSpecialRow || Node is null ? "" : Node.FileCount.ToString("N0", Loc.I.Culture);

    /// <summary>Extra note: cloud-only data, links, unreadable folders.</summary>
    public string Note
    {
        get
        {
            if (Node is null) return "";
            if (Node.IsLink) return Loc.I["Link"];
            if (Node.AccessDenied) return Loc.I["AccessDenied"];
            if (Node.CloudOnlySize > 0) return Loc.I.F("CloudOnly", ByteFormat.Format(Node.CloudOnlySize, Loc.I.Culture));
            return "";
        }
    }

    public bool CanDelete => !IsSpecialRow && Node?.Parent is not null;

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) EnsureLoaded();
    }

    public void EnsureLoaded()
    {
        if (_loaded || Node is null) return;
        _loaded = true;
        Rebuild();
    }

    /// <summary>
    /// Points this row at a freshly scanned copy of its folder. The row object stays, so it keeps its place in the
    /// tree, its selection and its expanded state, and so do the loaded rows below it.
    /// </summary>
    public void Rebind(DirNode fresh)
    {
        if (IsSpecialRow || ReferenceEquals(Node, fresh)) return;
        Node = fresh;
        SyncChildren();
    }

    /// <summary>
    /// Brings the child rows in line with the model after a change: rows of folders that are still there are kept
    /// (with their expanded state), new folders get new rows, and the order follows the new sizes.
    /// </summary>
    public void SyncChildren()
    {
        if (Node is null) return;
        if (_loaded)
        {
            Rebuild();
            return;
        }
        // not expanded yet: only the placeholder that gives the row its chevron
        var wantPlaceholder = Node.Children.Count > 0;
        if (wantPlaceholder && Children.Count == 0) Children.Add(Placeholder);
        else if (!wantPlaceholder && Children.Count > 0) Children.Clear();
        OnPropertyChanged(string.Empty);
    }

    private void Rebuild()
    {
        if (Node is null) return;
        // Case-sensitive keys: a case-sensitive folder (from WSL) can hold both "Docs" and "docs" as separate children;
        // the scanner builds their paths from enumeration, so their case is stable and they must keep separate rows.
        var existing = new Dictionary<string, FolderItemViewModel>(StringComparer.Ordinal);
        FolderItemViewModel? filesRow = null;
        FolderItemViewModel? unaccountedRow = null;
        foreach (var c in Children)
        {
            if (c.IsFilesRow) filesRow = c;
            else if (c.IsUnaccountedRow) unaccountedRow = c;
            else if (c.Node is not null) existing[c.Node.FullPath] = c;
        }

        var rows = new List<FolderItemViewModel>(Node.Children.Count + 2);
        foreach (var child in Node.Children)
        {
            // Matched by path, so a folder that was scanned again keeps its row; removed once matched, so one existing
            // row is never reused for two children.
            if (existing.Remove(child.FullPath, out var row)) row.Rebind(child);
            else row = new FolderItemViewModel(child, this);
            rows.Add(row);
        }
        if (Node.OwnFilesSize > 0 && Node.Children.Count > 0)
        {
            filesRow ??= new FolderItemViewModel(this);
            var index = rows.TakeWhile(r => r.Size >= Node.OwnFilesSize).Count();
            rows.Insert(index, filesRow);
        }
        if (_unaccountedSize > 0)
        {
            // Keep the same row object and change its size in place, so only a Move (not a remove+insert) reaches the
            // tree: removing it while a sibling's container is detached makes WPF select the drive root.
            if (unaccountedRow is null) unaccountedRow = new FolderItemViewModel(this, _unaccountedSize);
            else unaccountedRow.SetUnaccounted(_unaccountedSize);
            var index = rows.TakeWhile(r => r.Size >= _unaccountedSize).Count();
            rows.Insert(index, unaccountedRow);
        }

        SyncList(Children, rows);
        OnPropertyChanged(string.Empty);
        foreach (var r in rows) r.OnPropertyChanged(string.Empty); // their share of this folder changed
    }

    /// <summary>
    /// Makes <paramref name="list"/> equal to <paramref name="wanted"/> with removals, moves and inserts only. A Clear
    /// would make the tree drop its selection (and pick the parent row instead) and lose its scroll position.
    /// </summary>
    private static void SyncList(ObservableCollection<FolderItemViewModel> list, List<FolderItemViewModel> wanted)
    {
        var keep = new HashSet<FolderItemViewModel>(wanted, ReferenceEqualityComparer.Instance);
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(list[i])) list.RemoveAt(i);
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < list.Count && ReferenceEquals(list[i], wanted[i])) continue;
            var at = -1;
            for (var j = i + 1; j < list.Count; j++)
            {
                if (!ReferenceEquals(list[j], wanted[i])) continue;
                at = j;
                break;
            }
            if (at >= 0) list.Move(at, i);
            else list.Insert(i, wanted[i]);
        }
    }

    /// <summary>Re-reads sizes and labels (after a language change) for this row and loaded rows below.</summary>
    public void Refresh()
    {
        if (IsFilesRow) Name = Loc.I["FilesHere"];
        if (IsUnaccountedRow) Name = Loc.I["UnaccountedRow"];
        OnPropertyChanged(string.Empty);
        if (!_loaded) return;
        foreach (var c in Children) c.Refresh();
    }

    /// <summary>The row for <paramref name="target"/> in this subtree, or null if it has none (its parent was never expanded).</summary>
    public FolderItemViewModel? FindRow(DirNode target)
    {
        if (ReferenceEquals(Node, target)) return this;
        if (!_loaded || Node is null || !Node.IsAncestorOrSelf(target)) return null;
        foreach (var c in Children)
        {
            if (c.IsSpecialRow || c.Node is null) continue;
            var hit = c.FindRow(target);
            if (hit is not null) return hit;
        }
        return null;
    }
}
