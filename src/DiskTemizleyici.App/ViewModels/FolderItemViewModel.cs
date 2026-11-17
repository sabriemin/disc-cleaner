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

    public DirNode? Node { get; }
    public FolderItemViewModel? Parent { get; }

    public int Depth => Parent is null ? 0 : Parent.Depth + 1;

    /// <summary>
    /// Rows are full width and indent only their chevron, so the size columns line up for every depth.
    /// </summary>
    public System.Windows.Thickness IndentMargin => new(Math.Min(Depth, 14) * IndentPerLevel, 0, 0, 0);

    public const double IndentPerLevel = 18;

    /// <summary>Segoe Fluent / MDL2 glyph for the row's icon tile.</summary>
    public string Glyph =>
        IsFilesRow ? "" :          // Document
        Node is null ? "" :
        Node.IsLink ? "" :          // Link
        Node.AccessDenied ? "" :    // Lock
        Node.Parent is null ? "" :  // HardDrive
        "";                         // Folder

    public bool HasNote => !string.IsNullOrEmpty(Note);
    public bool IsFilesRow { get; }
    public string Name { get; private set; }
    public ObservableCollection<FolderItemViewModel> Children { get; } = new();

    public long Size => IsFilesRow ? Parent?.Node?.OwnFilesSize ?? 0 : Node?.Size ?? 0;

    public string SizeText => ByteFormat.Format(Size, Loc.I.Culture);

    /// <summary>Share of the parent folder, 0–100.</summary>
    public double Share
    {
        get
        {
            var parentSize = IsFilesRow ? Parent?.Node?.Size : Node?.Parent?.Size;
            return parentSize is > 0 ? 100.0 * Size / parentSize.Value : 100.0;
        }
    }

    public string ShareText => Share.ToString("0.0", Loc.I.Culture) + " %";

    public string FilesText => IsFilesRow || Node is null ? "" : Node.FileCount.ToString("N0", Loc.I.Culture);

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

    public bool CanDelete => !IsFilesRow && Node?.Parent is not null;

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
        Children.Clear();
        foreach (var child in Node.Children) Children.Add(new FolderItemViewModel(child, this));
        if (Node.OwnFilesSize > 0 && Node.Children.Count > 0)
        {
            var filesRow = new FolderItemViewModel(this);
            var index = Children.TakeWhile(c => c.Size >= filesRow.Size).Count();
            Children.Insert(index, filesRow);
        }
    }

    /// <summary>Re-reads sizes and labels (after a deletion or a language change) for this row and loaded rows below.</summary>
    public void Refresh()
    {
        if (IsFilesRow) Name = Loc.I["FilesHere"];
        OnPropertyChanged(string.Empty);
        if (!_loaded) return;
        foreach (var c in Children) c.Refresh();
    }

    /// <summary>Removes this folder from the tree after it was deleted.</summary>
    public void RemoveFromTree()
    {
        if (Node?.Parent is null || Parent is null) return;
        Node.Parent.RemoveChild(Node);
        Parent.Children.Remove(this);
        for (var p = Parent; p is not null; p = p.Parent) p.Refresh();
    }
}
