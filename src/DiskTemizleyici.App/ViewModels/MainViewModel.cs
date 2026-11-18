using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskTemizleyici.App.Localization;
using DiskTemizleyici.App.Services;
using DiskTemizleyici.Core;
using DiskTemizleyici.Core.Cleanup;
using DiskTemizleyici.Core.Scanning;

namespace DiskTemizleyici.App.ViewModels;

public enum DialogKind
{
    Question,
    Danger,
    Error,
    Info,
}

/// <summary>What the view model needs from the window.</summary>
public interface IShell
{
    bool Confirm(string title, string message, DialogKind kind, string confirmText);
    void Show(string title, string message, DialogKind kind);
    string? PickFolder();
    IntPtr OwnerHandle { get; }
    void ApplyGlass(bool glass);
}

public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>Below this, the "unaccounted space" row is noise.</summary>
    private const long MinUnaccountedBytes = 64L * 1024 * 1024;

    private readonly IShell _shell;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _progressTimer;
    private DiskScanner? _scanner;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _evalCts;
    private CancellationTokenSource? _cleanCts;
    private CancellationTokenSource? _refreshCts;
    private DirNode? _root;
    private List<CleanupRule> _rules = new();
    private readonly List<string> _ruleProblems = new();
    // A full suggestion check has finished for the current scan, so a new scan need not start one again on its own.
    private bool _suggestionsEvaluated;
    private readonly List<LargeFileViewModel> _allLargeFiles = new();
    private readonly string? _signedInUser;

    // Texts are kept as recipes, so a language switch can rebuild them instead of wiping them.
    private Func<string> _islandRecipe;
    private Func<string> _statusRecipe = () => "";

    public MainViewModel(IShell shell, AppSettings settings)
    {
        _shell = shell;
        _settings = settings;
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _progressTimer.Tick += (_, _) => UpdateScanProgress();
        Loc.I.LanguageChanged += (_, _) => OnLanguageChanged();

        _selectedAccent = ThemeManager.FindAccent(settings.Accent);
        _isGlass = settings.Glass && ThemeManager.GlassSupported;
        _islandRecipe = () => Loc.I["IslandReady"];
        _islandText = _islandRecipe();

        if (IsAdmin)
        {
            var signedIn = SessionUser.SignedInUserName();
            if (!string.IsNullOrEmpty(signedIn) && !string.Equals(signedIn, Environment.UserName, StringComparison.OrdinalIgnoreCase))
                _signedInUser = signedIn;
        }
        if (_signedInUser is not null) SetStatus(() => OtherUserWarning);

        LoadDrives();
        LoadRules();
    }

    public ObservableCollection<DriveViewModel> Drives { get; } = new();
    public ObservableCollection<FolderItemViewModel> Tree { get; } = new();
    public ObservableCollection<LargeFileViewModel> LargeFiles { get; } = new();
    public ObservableCollection<CandidateViewModel> Candidates { get; } = new();
    public ObservableCollection<FileCategoryViewModel> FileTypes { get; } = new();

    public bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>"0.2.0": release builds get it from the v0.2.0 tag (see .github/workflows/build.yml).</summary>
    public string AppVersion { get; } = CrashReporter.FullVersion.Split('+')[0]; // the SDK appends "+<commit>"

    /// <summary>Set when "Run as administrator" started the app under another account than the signed-in one.</summary>
    public string OtherUserWarning => _signedInUser is null ? "" : Loc.I.F("OtherUserWarning", Environment.UserName, _signedInUser);

    // ---------------------------------------------------------------- state

    /// <summary>Scanning, cleaning, deleting or rescanning: the tree is being changed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(ScanFolderCommand), nameof(CleanSelectedCommand), nameof(RefreshSuggestionsCommand),
        nameof(MoveToRecycleCommand), nameof(DeletePermanentCommand), nameof(RescanCommand), nameof(RestartAsAdminCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(ShowStop))]
    private bool _isScanning;

    /// <summary>Cleanup suggestions are being worked out. This does not block anything else.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStop))]
    [NotifyCanExecuteChangedFor(nameof(CleanSelectedCommand))]
    private bool _isEvaluating;

    partial void OnIsEvaluatingChanged(bool value) => UpdateSuggestionsMessage();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStop))]
    private bool _isCleaning;

    /// <summary>Folders are scanned again after a change; Esc / Stop ends that early.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStop))]
    private bool _isRefreshing;

    /// <summary>Something that Esc / Stop can stop is running.</summary>
    public bool ShowStop => IsScanning || IsEvaluating || IsCleaning || IsRefreshing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _hasScan;

    /// <summary>Nothing scanned yet and nothing running: show the welcome card.</summary>
    public bool ShowEmpty => !HasScan && !IsScanning;

    [ObservableProperty]
    private DriveViewModel? _selectedDrive;

    /// <summary>Short status shown in the island at the top of the window.</summary>
    [ObservableProperty]
    private string _islandText;

    /// <summary>Longer status line under the drive cards.</summary>
    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _scanFilesText = "";

    [ObservableProperty]
    private string _scanBytesText = "";

    [ObservableProperty]
    private string _scanPath = "";

    [ObservableProperty]
    private bool _permanentDelete;

    [ObservableProperty]
    private string _selectedTotalText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReclaimable))]
    private string _reclaimableText = "";

    public bool HasReclaimable => !string.IsNullOrEmpty(ReclaimableText);

    [ObservableProperty]
    private string _suggestionsMessage = "";

    /// <summary>0 = folders, 1 = large files, 2 = suggestions, 3 = file types.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentItem))]
    private int _selectedView;

    /// <summary>Folders that could not be read in the last scan.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDeniedButton), nameof(DeniedButtonText))]
    private long _deniedFolders;

    public bool ShowDeniedButton => DeniedFolders > 0 && !IsAdmin && !IsDemo;

    public string DeniedButtonText => Loc.I.FN("DeniedButton", DeniedFolders, DeniedFolders);

    // keyboard: the row the shortcuts act on
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentItem))]
    private FolderItemViewModel? _selectedFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentItem))]
    private LargeFileViewModel? _selectedLargeFile;

    /// <summary>The selected row of the visible view; Del, Shift+Del, Enter, Ctrl+C and F5 act on it.</summary>
    public object? CurrentItem => SelectedView switch
    {
        0 => SelectedFolder,
        1 => SelectedLargeFile,
        _ => null,
    };

    private void SetIsland(Func<string> text)
    {
        _islandRecipe = text;
        IslandText = text();
    }

    private void SetStatus(Func<string> text)
    {
        _statusRecipe = text;
        Status = text();
    }

    // ---------------------------------------------------------------- appearance

    public IReadOnlyList<AccentOption> Accents => ThemeManager.Accents;

    public bool GlassSupported => ThemeManager.GlassSupported;

    [ObservableProperty]
    private AccentOption _selectedAccent;

    partial void OnSelectedAccentChanged(AccentOption value)
    {
        if (value is null) return;
        ThemeManager.ApplyAccent(value);
        _settings.Accent = value.Id;
        _settings.Save();
    }

    [ObservableProperty]
    private bool _isGlass;

    partial void OnIsGlassChanged(bool value)
    {
        _shell.ApplyGlass(value);
        _settings.Glass = value;
        _settings.Save();
    }

    public string LanguageCode
    {
        get => Loc.I.Language;
        set
        {
            if (value is null || value == Loc.I.Language) return;
            Loc.I.Language = value;
            _settings.Language = value;
            _settings.Save();
        }
    }

    // ---------------------------------------------------------------- drives & rules

    private void LoadDrives()
    {
        var current = SelectedDrive?.RootPath;
        Drives.Clear();
        // The type first: IsReady touches the drive, and a disconnected network drive can hang for a while.
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady))
            Drives.Add(new DriveViewModel(d));
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        SelectedDrive = Drives.FirstOrDefault(d => d.RootPath == current)
                        ?? Drives.FirstOrDefault(d => string.Equals(d.RootPath, systemRoot, StringComparison.OrdinalIgnoreCase))
                        ?? Drives.FirstOrDefault();
    }

    private void LoadRules()
    {
        try
        {
            _ruleProblems.Clear();
            _rules = RuleLoader.LoadAll(Path.Combine(AppContext.BaseDirectory, "rules.json"), _ruleProblems);
            foreach (var problem in _ruleProblems) CrashReporter.Diagnostic("rules.json: " + problem);
        }
        catch (Exception ex)
        {
            _rules = new List<CleanupRule>();
            CrashReporter.Diagnostic("built-in rules failed to load: " + ex.Message);
            _shell.Show(Loc.I["Error"], ex.Message, DialogKind.Error);
        }
    }

    // ---------------------------------------------------------------- scanning

    private bool CanStart() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task ScanAsync() => SelectedDrive is null ? Task.CompletedTask : RunScanAsync(SelectedDrive.RootPath);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task ScanFolderAsync()
    {
        var folder = _shell.PickFolder();
        return folder is null ? Task.CompletedTask : RunScanAsync(folder);
    }

    /// <summary>Stops a running scan, suggestion check, clean-up or rescan (Esc). A clean-up stops between items.</summary>
    [RelayCommand]
    private void Cancel()
    {
        _scanCts?.Cancel();
        _evalCts?.Cancel();
        _cleanCts?.Cancel();
        _refreshCts?.Cancel();
    }

    /// <summary>Scans a folder given on the command line.</summary>
    public Task StartScanAsync(string path) => IsBusy ? Task.CompletedTask : RunScanAsync(path);

    private async Task RunScanAsync(string path)
    {
        if (IsDemo)
        {
            LoadDemo();
            return;
        }

        IsBusy = true;
        IsScanning = true;
        SelectedView = 0;
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var scanner = _scanner = new DiskScanner();
        Tree.Clear();
        _allLargeFiles.Clear();
        LargeFiles.Clear();
        FileTypes.Clear();
        OnPropertyChanged(nameof(HasFileTypes));
        _root?.Detach();
        _root = null;
        HasScan = false;
        DeniedFolders = 0;
        _suggestionsEvaluated = false;
        SetStatus(() => OtherUserWarning);
        ScanPath = path;
        UpdateScanProgress();
        _progressTimer.Start();
        var completed = false;
        try
        {
            var result = await Task.Run(() => scanner.Scan(path, cts.Token), cts.Token);
            _root = result.Root;

            var rootVm = new FolderItemViewModel(result.Root, null);
            Tree.Add(rootVm);
            rootVm.IsExpanded = true;
            _allLargeFiles.AddRange(result.LargestFiles.Select(f => new LargeFileViewModel(f)));
            LargeFilesFilter = null;
            ApplyLargeFilesFilter();
            BuildFileTypes(result.FileTypes);
            HasScan = true;
            DeniedFolders = result.InaccessibleDirectories;
            UpdateUnaccounted();

            var size = result.Root.Size;
            var files = result.Root.FileCount;
            var seconds = result.Elapsed.TotalSeconds;
            var denied = result.InaccessibleDirectories;
            var shown = result.Root.FullPath;
            SetIsland(() => Loc.I.F("IslandDone", ByteFormat.Format(size, Loc.I.Culture), seconds));
            SetStatus(() =>
            {
                var text = Loc.I.FN("ScanDone", files, shown, ByteFormat.Format(size, Loc.I.Culture), files, seconds, denied);
                if (_signedInUser is not null) text += "  " + OtherUserWarning;
                return text;
            });
            CrashReporter.Diagnostic($"scan {shown}: {size:N0} bytes, {files:N0} files, {denied:N0} unreadable folders, {seconds:0.0} s");
            completed = true;
        }
        catch (Exception ex) when (IsCancellation(ex))
        {
            SetIsland(() => Loc.I["IslandCancelled"]);
        }
        catch (Exception ex)
        {
            SetIsland(() => Loc.I["IslandFailed"]);
            var message = ex.Message;
            SetStatus(() => Loc.I.F("ScanFailed", message));
            CrashReporter.Diagnostic($"scan failed: {ex}");
        }
        finally
        {
            _progressTimer.Stop();
            _scanner = null;
            if (ReferenceEquals(_scanCts, cts)) _scanCts = null;
            cts.Dispose();
            IsScanning = false;
            IsBusy = false;
        }

        // Suggestions run on their own; the Scan command (and its button) is free again right away.
        // Not after Stop: stopping must not start something new. A lone Recycle Bin card (added after a delete) must
        // not count as "already checked", so this hangs on a completed-check flag, not on the card count.
        if (completed && !_suggestionsEvaluated && !IsEvaluating) _ = EvaluateSuggestionsAsync();
    }

    private void UpdateScanProgress()
    {
        var s = _scanner;
        if (s is null) return;
        var files = s.FilesScanned;
        var bytes = s.BytesScanned;
        ScanFilesText = files.ToString("N0", Loc.I.Culture);
        ScanBytesText = ByteFormat.Format(bytes, Loc.I.Culture);
        if (!string.IsNullOrEmpty(s.CurrentPath)) ScanPath = s.CurrentPath;
        SetIsland(() => Loc.I.FN("IslandScanning", files, files, ByteFormat.Format(bytes, Loc.I.Culture)));
    }

    private static bool IsCancellation(Exception ex) => ex switch
    {
        OperationCanceledException => true,
        AggregateException a => a.Flatten().InnerExceptions.All(e => e is OperationCanceledException),
        _ => false,
    };

    /// <summary>On a whole-drive scan: used space the scan did not find, shown as its own row.</summary>
    private void UpdateUnaccounted()
    {
        var rootRow = Tree.FirstOrDefault();
        if (rootRow is null || _root is null) return;
        var driveRoot = Path.GetPathRoot(_root.FullPath);
        // only for a local drive letter: a network share (\\nas\media) has no DriveInfo
        if (IsDemo || driveRoot is null || driveRoot.StartsWith(@"\\", StringComparison.Ordinal)
            || !string.Equals(_root.FullPath, driveRoot, StringComparison.OrdinalIgnoreCase))
        {
            rootRow.UnaccountedSize = 0;
            return;
        }
        try
        {
            var drive = new DriveInfo(driveRoot);
            var missing = drive.TotalSize - drive.TotalFreeSpace - _root.Size;
            rootRow.UnaccountedSize = missing >= MinUnaccountedBytes ? missing : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            rootRow.UnaccountedSize = 0;
        }
    }

    // ---------------------------------------------------------------- file types

    public bool HasFileTypes => FileTypes.Count > 0;

    /// <summary>Something was deleted or rescanned after the last full scan, so the file-type numbers are out of date.</summary>
    [ObservableProperty]
    private bool _fileTypesStale;

    private void BuildFileTypes(IReadOnlyList<FileTypeStat> stats)
    {
        FileTypesStale = false;
        FileTypes.Clear();
        var total = stats.Sum(s => s.Bytes);
        foreach (var group in stats.GroupBy(s => s.Category).OrderByDescending(g => g.Sum(s => s.Bytes)))
            FileTypes.Add(new FileCategoryViewModel(group.Key, group.ToList(), total));
        OnPropertyChanged(nameof(HasFileTypes));
    }

    [RelayCommand]
    private void ToggleCategory(FileCategoryViewModel? category)
    {
        if (category is not null) category.IsExpanded = !category.IsExpanded;
    }

    /// <summary>".mp4" filters the large-file list to that extension; null shows everything.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLargeFilesFilter), nameof(LargeFilesFilterText))]
    private string? _largeFilesFilter;

    public bool HasLargeFilesFilter => LargeFilesFilter is not null;

    public string LargeFilesFilterText => LargeFilesFilter is null ? ""
        : Loc.I.F("FilterLabel", LargeFilesFilter.Length == 0 ? Loc.I["NoExtension"] : LargeFilesFilter);

    public bool ShowFilterEmpty => HasLargeFilesFilter && LargeFiles.Count == 0;

    [RelayCommand]
    private void FilterByExtension(FileExtensionViewModel? extension)
    {
        if (extension is null) return;
        LargeFilesFilter = extension.Stat.Extension;
        ApplyLargeFilesFilter();
        SelectedView = 1;
    }

    [RelayCommand]
    private void ClearLargeFilesFilter()
    {
        LargeFilesFilter = null;
        ApplyLargeFilesFilter();
    }

    private void ApplyLargeFilesFilter()
    {
        LargeFiles.Clear();
        foreach (var f in _allLargeFiles)
        {
            if (LargeFilesFilter is null || f.ExtensionKey == LargeFilesFilter) LargeFiles.Add(f);
        }
        OnPropertyChanged(nameof(ShowFilterEmpty));
    }

    // ---------------------------------------------------------------- suggestions

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task RefreshSuggestionsAsync() => EvaluateSuggestionsAsync();

    /// <summary>
    /// Works the suggestions out in the background. Cards appear as soon as each rule is done; browsing, scanning
    /// and deleting keep working, and Esc stops it.
    /// </summary>
    private async Task EvaluateSuggestionsAsync()
    {
        if (IsDemo)
        {
            LoadDemo();
            return;
        }

        _evalCts?.Cancel();
        var cts = new CancellationTokenSource();
        _evalCts = cts;
        Candidates.Clear();
        IsEvaluating = true;
        UpdateSelectedTotal();

        IProgress<CleanupCandidate> progress = new Progress<CleanupCandidate>(c =>
        {
            if (!cts.IsCancellationRequested) AddCandidate(c);
        });
        var rules = _rules;
        try
        {
            await Task.Run(() => RuleEvaluator.EvaluateAllAsync(rules, progress.Report, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            CrashReporter.Diagnostic("suggestions failed: " + ex);
            _shell.Show(Loc.I["Error"], ex.Message, DialogKind.Error);
        }
        finally
        {
            if (ReferenceEquals(_evalCts, cts))
            {
                _evalCts = null;
                IsEvaluating = false;
                // Only a check that ran to the end counts: a cancelled one lets the next scan check again.
                if (!cts.IsCancellationRequested) _suggestionsEvaluated = true;
            }
        }
        UpdateSelectedTotal();
        UpdateSuggestionsMessage();
    }

    private void AddCandidate(CleanupCandidate candidate)
    {
        if (candidate.Error is not null) CrashReporter.Diagnostic($"rule {candidate.Rule.Id} could not be checked: {candidate.Error}");
        // There is only ever one Recycle Bin card: a fresh one (after a delete) replaces the old, never doubles it.
        if (candidate.Rule.Mode == RuleMode.RecycleBin
            && Candidates.FirstOrDefault(c => c.Candidate.Rule.Mode == RuleMode.RecycleBin) is { } existingBin)
            Candidates.Remove(existingBin);
        var vm = new CandidateViewModel(candidate, UpdateSelectedTotal, IsAdmin);
        // Cleanable cards largest first; cards that only explain a problem go to the end.
        static (int, long) Key(CandidateViewModel c) => (c.IsActionable ? 0 : 1, -c.Candidate.Size);
        var key = Key(vm);
        var index = 0;
        while (index < Candidates.Count && Key(Candidates[index]).CompareTo(key) <= 0) index++;
        Candidates.Insert(index, vm);
        UpdateSelectedTotal();
        UpdateSuggestionsMessage();
    }

    private void UpdateSuggestionsMessage()
    {
        var parts = new List<string>();
        if (_signedInUser is not null) parts.Add(OtherUserWarning);
        if (_ruleProblems.Count > 0) parts.Add(Loc.I.FN("RuleProblems", _ruleProblems.Count, _ruleProblems.Count, _ruleProblems[0]));
        parts.Add(IsEvaluating ? Loc.I["EvaluatingHint"]
            : Candidates.Count == 0 ? Loc.I["NoSuggestions"]
            : Loc.I["SuggestionsHint"]);
        SuggestionsMessage = string.Join("\n", parts);
    }

    private void UpdateSelectedTotal()
    {
        var selected = Candidates.Where(c => c.IsSelected && c.IsActionable).Sum(c => c.Candidate.Size);
        var all = Candidates.Where(c => c.IsActionable).Sum(c => c.Candidate.Size);
        SelectedTotalText = ByteFormat.Format(selected, Loc.I.Culture);
        ReclaimableText = all > 0 ? ByteFormat.Format(all, Loc.I.Culture) : "";
        CleanSelectedCommand.NotifyCanExecuteChanged();
    }

    private bool CanClean() => !IsBusy && !IsEvaluating && Candidates.Any(c => c.IsSelected && c.IsActionable);

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanSelectedAsync()
    {
        var selected = Candidates.Where(c => c.IsSelected && c.IsActionable).Select(c => c.Candidate).ToList();
        if (selected.Count == 0 || IsDemo) return;
        var selectedBefore = SelectedFolder;

        var emptyBin = selected.Any(c => c.Rule.Mode == RuleMode.RecycleBin);
        var others = selected.Where(c => c.Rule.Mode != RuleMode.RecycleBin).ToList();
        var mode = PermanentDelete ? DeleteMode.Permanent : DeleteMode.RecycleBin;
        var itemCount = others.Sum(c => (long)c.Items.Count);
        var othersSize = others.Sum(c => c.Size);

        // The bin is measured now, not when its card was made: things may have been moved into it since.
        var binSize = emptyBin ? RecycleBin.Query().Size : 0;
        var lines = string.Join("\n", selected.Select(c =>
            $"•  {c.Rule.GetName(Loc.I.Language)}  —  {ByteFormat.Format(c.Rule.Mode == RuleMode.RecycleBin ? binSize : c.Size, Loc.I.Culture)}"));
        var message = lines;
        if (others.Count > 0)
        {
            var key = mode == DeleteMode.Permanent ? "ConfirmPermanent" : "ConfirmRecycle";
            message += "\n\n" + Loc.I.FN(key, itemCount, itemCount, ByteFormat.Format(othersSize, Loc.I.Culture));
        }
        if (emptyBin) message += "\n\n" + Loc.I.F("ConfirmEmptyBin", ByteFormat.Format(binSize, Loc.I.Culture));

        var dangerous = mode == DeleteMode.Permanent || emptyBin;
        if (!_shell.Confirm(Loc.I["ConfirmCleanTitle"], message, dangerous ? DialogKind.Danger : DialogKind.Question,
                Loc.I[dangerous ? "DialogDelete" : "DialogContinue"]))
            return;

        // Explorer or a cloud sync can move more into the bin while the dialog is open: never destroy more than was
        // confirmed. "approvedBin" is the largest size the user has agreed to empty; it is re-checked just before emptying.
        var binSkipped = false;
        var approvedBin = binSize;
        if (emptyBin)
        {
            var binNow = RecycleBin.Query().Size;
            if (binNow > binSize)
            {
                if (_shell.Confirm(Loc.I["BinGrewTitle"], Loc.I.F("BinGrewText", ByteFormat.Format(binNow, Loc.I.Culture)),
                        DialogKind.Danger, Loc.I["DialogDelete"]))
                    approvedBin = binNow;
                else
                {
                    emptyBin = false;
                    binSkipped = true;
                }
            }
        }
        if (!emptyBin && others.Count == 0)
        {
            SetStatus(() => Loc.I["ResultBinSkipped"]);
            return;
        }

        IsBusy = true;
        IsCleaning = true;
        ProgressValue = 0;
        var cts = new CancellationTokenSource();
        _cleanCts = cts;
        var token = cts.Token;
        var freeBefore = FreeSpaceNow();
        var reports = new List<DeleteReport>();
        var binFailed = false;
        long moved = 0;
        try
        {
            try
            {
                var owner = _shell.OwnerHandle;

                // 1. Empty the Recycle Bin first. Doing it last would destroy what this clean-up just moved into it.
                //    One last check: if it grew past what the user approved (sync while the prompt was open), skip it.
                if (emptyBin && !token.IsCancellationRequested)
                {
                    if (RecycleBin.Query().Size > approvedBin)
                    {
                        emptyBin = false;
                        binSkipped = true;
                    }
                    else
                    {
                        binFailed = !await RunOnStaThread(RecycleBin.Empty);
                    }
                }
                var binBefore = RecycleBin.Query().Size;

                // 2. Everything else.
                var total = Math.Max(1, others.Sum(c => c.Items.Count));
                var doneBefore = 0;
                foreach (var c in others)
                {
                    if (token.IsCancellationRequested) break;
                    var offset = doneBefore;
                    var progress = new Progress<int>(n =>
                    {
                        ProgressValue = 100.0 * (offset + n) / total;
                        var percent = ProgressValue;
                        SetIsland(() => Loc.I.F("IslandCleaning", percent));
                    });
                    var items = c.Items;
                    // Checked again right before each item: still inside the rule's folders, with no link swapped in on
                    // the way, and (for rules with an age limit) not changed since the suggestion was worked out.
                    var options = new DeleteOptions(c.Roots, c.Rule.MinAgeDays > 0 ? DateTime.UtcNow.AddDays(-c.Rule.MinAgeDays) : null);
                    // The Recycle Bin is a shell operation and must run on an STA thread.
                    var report = await RunOnStaThread(() => Deleter.Delete(items, mode, progress, token, owner, options));
                    reports.Add(report);
                    if (report.Cancelled) break; // "No" in a Windows prompt stops the whole clean-up
                    doneBefore += items.Count;
                }

                if (mode == DeleteMode.RecycleBin) moved = Math.Max(0, RecycleBin.Query().Size - binBefore);
            }
            catch (Exception ex)
            {
                CrashReporter.Diagnostic("clean-up failed: " + ex);
                _shell.Show(Loc.I["Error"], ex.Message, DialogKind.Error);
            }
            finally
            {
                if (ReferenceEquals(_cleanCts, cts)) _cleanCts = null;
                IsCleaning = false;
                ProgressValue = 0;
            }

            ReportResult(FreedSince(freeBefore), moved, reports, binFailed, binSkipped, cancelled: token.IsCancellationRequested);
            LoadDrives();

            var changed = others.SelectMany(c => c.Roots).ToList();
            if (mode == DeleteMode.RecycleBin || emptyBin) changed.AddRange(RecycleBinFolders());
            // Still busy: nothing else may change the tree while it is brought up to date.
            await RefreshAfterChangeAsync(changed);
            // A selected folder that the clean-up removed must not leave Del pointed at the parent WPF selects instead.
            ClearSelectionIfRowGone(selectedBefore);
        }
        finally
        {
            cts.Dispose();
            IsBusy = false;
        }
        _ = EvaluateSuggestionsAsync(); // recheck in the background
    }

    // ---------------------------------------------------------------- tree / file actions

    private bool CanUseItem(object? item) => item switch
    {
        FolderItemViewModel f => f.FolderNode is not null,
        LargeFileViewModel => true,
        _ => false,
    };

    private bool CanDeleteItem(object? item) => !IsBusy && !IsDemo && item switch
    {
        FolderItemViewModel f => f.CanDelete,
        LargeFileViewModel => true,
        _ => false,
    };

    private bool CanRescanItem(object? item) => !IsBusy && !IsDemo && item is FolderItemViewModel { FolderNode: not null };

    [RelayCommand(CanExecute = nameof(CanUseItem))]
    private void OpenInExplorer(object? item)
    {
        var path = PathOf(item);
        if (path is null) return;
        var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    [RelayCommand(CanExecute = nameof(CanUseItem))]
    private void CopyPath(object? item)
    {
        var path = PathOf(item);
        if (path is null) return;
        // another program can be holding the clipboard for a moment
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(path);
                return;
            }
            catch (ExternalException)
            {
                Thread.Sleep(60);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteItem))]
    private Task MoveToRecycle(object? item) => DeleteItemAsync(item, DeleteMode.RecycleBin);

    [RelayCommand(CanExecute = nameof(CanDeleteItem))]
    private Task DeletePermanent(object? item) => DeleteItemAsync(item, DeleteMode.Permanent);

    /// <summary>Scans one folder again and updates its numbers in place (F5).</summary>
    [RelayCommand(CanExecute = nameof(CanRescanItem))]
    private async Task Rescan(object? item)
    {
        if (IsBusy || IsDemo || _root is null) return;
        var node = (item as FolderItemViewModel)?.FolderNode;
        if (node is null) return;

        var selectedBefore = SelectedFolder;
        IsBusy = true;
        var previous = _islandRecipe;
        SetIsland(() => Loc.I["IslandRescanning"]);
        try
        {
            PruneLargeFiles();
            await RescanNodesAsync(new[] { node });
            // F5 on a folder deleted in Explorer removes its row; don't let the selection fall to its parent.
            ClearSelectionIfRowGone(selectedBefore);
        }
        finally
        {
            IsBusy = false;
            SetIsland(previous);
        }
    }

    /// <summary>
    /// F5: rescan the selected folder; in Suggestions recheck them; elsewhere scan again what was scanned last
    /// (the folder that was picked, not the drive card), or the selected drive if nothing was scanned yet.
    /// </summary>
    [RelayCommand]
    private async Task RefreshCurrent()
    {
        if (IsBusy) return;
        if (SelectedView == 0 && SelectedFolder is { FolderNode: not null } folder && HasScan) await RescanCommand.ExecuteAsync(folder);
        else if (SelectedView == 2) await RefreshSuggestionsCommand.ExecuteAsync(null);
        else if (HasScan && _root is not null) await RunScanAsync(_root.FullPath);
        else await ScanCommand.ExecuteAsync(null);
    }

    /// <summary>Ctrl+1 … Ctrl+4.</summary>
    [RelayCommand]
    private void ShowView(string? view)
    {
        if (int.TryParse(view, out var index) && index is >= 0 and <= 3) SelectedView = index;
    }

    private async Task DeleteItemAsync(object? item, DeleteMode mode)
    {
        if (IsBusy || IsDemo) return;
        var path = PathOf(item);
        if (path is null || item is FolderItemViewModel { CanDelete: false }) return;

        if (!SafetyGuard.CanDelete(path, out var reason))
        {
            _shell.Show(Loc.I["BlockedTitle"], path + "\n\n" + BlockText(reason), DialogKind.Error);
            return;
        }

        long size, count, cloudCount = 0, cloudSize = 0;
        var holdsLinuxDisks = false;
        switch (item)
        {
            case FolderItemViewModel { Node: { } node } f:
                size = f.Size;
                count = Math.Max(1, node.FileCount);
                cloudCount = node.CloudOnlyCount;
                cloudSize = node.CloudOnlySize;
                holdsLinuxDisks = CriticalFiles.HoldsLinuxDisks(path)
                    || _allLargeFiles.Any(l => l.Critical is CriticalKind.LinuxDisk or CriticalKind.VirtualDisk && IsInside(l.File.FullPath, path));
                break;
            case LargeFileViewModel l:
                size = l.Size;
                count = 1;
                // A whole-disk file: say plainly what deleting it means before the usual question.
                if (l.Critical is CriticalKind.LinuxDisk or CriticalKind.VirtualDisk)
                {
                    var key = l.Critical == CriticalKind.LinuxDisk ? "CriticalLinuxText" : "CriticalVmText";
                    if (!_shell.Confirm(Loc.I["CriticalTitle"], Loc.I.F(key, path), DialogKind.Danger, Loc.I["DeleteAnyway"])) return;
                    holdsLinuxDisks = true;
                }
                break;
            default:
                return;
        }

        var permanent = mode == DeleteMode.Permanent;
        var confirmKey = permanent ? "ConfirmPermanent" : "ConfirmRecycle";
        var message = path + "\n\n" + Loc.I.FN(confirmKey, count, count, ByteFormat.Format(size, Loc.I.Culture));
        var dangerous = permanent || holdsLinuxDisks;
        if (!permanent && !RecycleBin.IsAvailableFor(path))
        {
            message += "\n\n" + Loc.I["NoRecycleBinWarning"];
            dangerous = true;
        }
        if (holdsLinuxDisks && item is FolderItemViewModel) message += "\n\n" + Loc.I["FolderHoldsLinuxDisks"];
        if (cloudCount > 0)
        {
            message += "\n\n" + Loc.I.FN("ConfirmCloudOnly", cloudCount, cloudCount, ByteFormat.Format(cloudSize, Loc.I.Culture));
            dangerous = true;
        }
        if (SafetyGuard.NeedsExtraWarning(path))
        {
            message += "\n\n" + Loc.I["ConfirmSystemArea"];
            dangerous = true;
        }
        if (!_shell.Confirm(Loc.I[permanent ? "DeletePermanentTitle" : "MoveToRecycleTitle"], message,
                dangerous ? DialogKind.Danger : DialogKind.Question, Loc.I[permanent ? "DialogDelete" : "DialogContinue"]))
            return;

        var selectedBefore = SelectedFolder;
        IsBusy = true;
        var freeBefore = FreeSpaceNow();
        var binBefore = RecycleBin.Query().Size;
        try
        {
            var owner = _shell.OwnerHandle;
            var report = await RunOnStaThread(() => Deleter.Delete(new[] { path }, mode, ownerWindow: owner));
            var moved = mode == DeleteMode.RecycleBin ? Math.Max(0, RecycleBin.Query().Size - binBefore) : 0;
            ReportResult(FreedSince(freeBefore), moved, new[] { report }, binFailed: false, binSkipped: false, cancelled: report.Cancelled);
            LoadDrives();

            // Only what changed is scanned again: the deleted folder itself (gone, or what could not be deleted),
            // and the Recycle Bin folder of that drive. A deleted file is simply taken out of its folder's totals.
            var changed = new List<string>();
            if (item is FolderItemViewModel) changed.Add(path);
            else if (!File.Exists(path) && _root?.Find(Path.GetDirectoryName(path) ?? "") is { } folder && folder.RemoveOwnFile(size))
            {
                FileTypesStale = true;
                SyncRowsFrom(folder);
            }
            if (moved > 0 && Path.GetPathRoot(path) is { Length: > 0 } drive) changed.Add(Path.Combine(drive, "$Recycle.Bin"));
            await RefreshAfterChangeAsync(changed);

            // The row the shortcuts acted on is gone: Del or Enter must not fall through to the folder the tree selects
            // instead (its parent) — whether a deleted folder's own row, or a "files here" row that a file delete dropped.
            ClearSelectionIfRowGone(selectedBefore);
            if (moved > 0) RefreshRecycleBinCard();
        }
        catch (Exception ex)
        {
            CrashReporter.Diagnostic("delete failed: " + ex);
            _shell.Show(Loc.I["Error"], ex.Message, DialogKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool IsInside(string path, string folder) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string? PathOf(object? item) => item switch
    {
        FolderItemViewModel f => f.FolderNode?.FullPath,
        LargeFileViewModel l => l.File.FullPath,
        _ => null,
    };

    private static string BlockText(BlockReason reason) => Loc.I["Block_" + (reason == BlockReason.None ? BlockReason.InvalidPath : reason)];

    // ---------------------------------------------------------------- keeping the tree true

    /// <summary>
    /// Rescans the given folders where they are in the tree, so sizes and order match the disk again. A folder that is
    /// not in the tree is skipped: rescanning its parent (or the whole drive) instead would be slow and is not needed.
    /// If the scanned folder itself lies inside one of them, it is scanned again.
    /// </summary>
    private async Task RefreshAfterChangeAsync(IEnumerable<string> folders)
    {
        PruneLargeFiles();
        if (_root is null) return;
        var root = _root;
        var nodes = new List<DirNode>();
        var inserts = new List<(DirNode Parent, string Path)>();
        foreach (var p in folders.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (root.Find(p) is { } node) nodes.Add(node);
            else if (IsInside(root.FullPath, p)) nodes.Add(root); // p is an ancestor of the scanned folder: rescan it whole
            // A folder under the scanned root with no node yet (a $Recycle.Bin that only just appeared): scan and add it,
            // so its bytes show as their own row instead of swelling "unaccounted space".
            else if (Directory.Exists(p) && Path.GetDirectoryName(p) is { } parentPath && root.Find(parentPath) is { } parent)
                inserts.Add((parent, p));
        }
        var distinct = nodes.Distinct().ToList();
        if (distinct.Count > 0 || inserts.Count > 0) await RescanNodesAsync(distinct, inserts);
        else UpdateUnaccounted();
    }

    private async Task RescanNodesAsync(IReadOnlyCollection<DirNode> nodes,
        IReadOnlyCollection<(DirNode Parent, string Path)>? inserts = null)
    {
        // Rescanning a folder covers everything below it.
        var top = nodes.Where(n => !nodes.Any(o => !ReferenceEquals(o, n) && o.IsAncestorOrSelf(n))).ToList();
        _refreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        IsRefreshing = true;
        try
        {
            foreach (var node in top)
            {
                if (!node.IsAttached) continue; // replaced meanwhile
                DirNode fresh;
                try
                {
                    var scanner = new DiskScanner();
                    fresh = await Task.Run(() => scanner.ScanDetached(node, cts.Token), cts.Token);
                }
                catch (Exception ex) when (IsCancellation(ex))
                {
                    break; // Esc: the folders not rescanned yet keep their old numbers
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                // The tree may have changed while this ran; a node that was replaced meanwhile must not be put back.
                if (!node.IsAttached) continue;
                if (fresh.Size != node.Size || fresh.FileCount != node.FileCount) FileTypesStale = true;

                if (node.Parent is null)
                {
                    // The scanned folder itself: its row stays, and so does what is expanded below it.
                    node.Detach();
                    _root = fresh;
                    if (Tree.FirstOrDefault() is { } rootRow)
                    {
                        rootRow.Rebind(fresh);
                    }
                    else
                    {
                        rootRow = new FolderItemViewModel(fresh, null);
                        Tree.Add(rootRow);
                        rootRow.IsExpanded = true;
                    }
                    continue;
                }

                var parent = node.Parent;
                var stillThere = Directory.Exists(node.FullPath);
                if (parent.ReplaceChild(node, stillThere ? fresh : null)) SyncRowsFrom(parent);
            }

            foreach (var (parent, path) in inserts ?? Array.Empty<(DirNode, string)>())
            {
                if (cts.IsCancellationRequested) break;
                if (!parent.IsAttached || parent.Find(path) is not null) continue; // replaced, or added meanwhile
                DirNode fresh;
                try
                {
                    var scanner = new DiskScanner();
                    var template = new DirNode(path, parent);
                    fresh = await Task.Run(() => scanner.ScanDetached(template, cts.Token), cts.Token);
                }
                catch (Exception ex) when (IsCancellation(ex))
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (!parent.IsAttached || parent.Find(path) is not null) continue;
                if (parent.AddScannedChild(fresh)) SyncRowsFrom(parent);
            }
        }
        finally
        {
            if (ReferenceEquals(_refreshCts, cts))
            {
                _refreshCts = null;
                IsRefreshing = false;
            }
            cts.Dispose();
        }
        UpdateUnaccounted();
    }

    /// <summary>
    /// After the sizes below <paramref name="changed"/> changed: updates its row, or the row of its nearest ancestor
    /// that has one (a folder whose parent was never expanded has no row), and every row above it.
    /// </summary>
    private void SyncRowsFrom(DirNode changed)
    {
        var rootRow = Tree.FirstOrDefault();
        if (rootRow is null) return;
        FolderItemViewModel? row = null;
        for (var n = changed; n is not null && row is null; n = n.Parent) row = rootRow.FindRow(n);
        for (; row is not null; row = row.Parent) row.SyncChildren();
    }

    private void ClearTreeSelection()
    {
        if (SelectedFolder is { } row) row.IsSelected = false;
        SelectedFolder = null;
    }

    /// <summary>
    /// When a refresh removes the selected row, WPF falls back to selecting its parent, so Del or Enter would then act
    /// on a folder the user never picked. If the row that was selected before the refresh has left the tree and the
    /// selection moved, drop it, so the shortcuts act on nothing until the user chooses a row again.
    /// </summary>
    private void ClearSelectionIfRowGone(FolderItemViewModel? before)
    {
        if (before is not null && !ReferenceEquals(SelectedFolder, before)
            && (before.IsSpecialRow || before.Node is { IsAttached: false }))
            ClearTreeSelection();
    }

    /// <summary>Shows the Recycle Bin card as the bin is now, after a delete moved something into it.</summary>
    private void RefreshRecycleBinCard()
    {
        if (IsDemo) return;
        var old = Candidates.FirstOrDefault(c => c.Candidate.Rule.Mode == RuleMode.RecycleBin);
        // Safe to run even while the background check is going: AddCandidate keeps a single Recycle Bin card.
        var rule = old?.Candidate.Rule ?? _rules.FirstOrDefault(r => r.Mode == RuleMode.RecycleBin);
        if (rule is null) return;
        if (old is not null) Candidates.Remove(old);
        if (RuleEvaluator.Evaluate(rule) is { Size: > 0 } fresh) AddCandidate(fresh);
        UpdateSelectedTotal();
        UpdateSuggestionsMessage();
    }

    private void PruneLargeFiles()
    {
        _allLargeFiles.RemoveAll(f => !File.Exists(f.File.FullPath));
        foreach (var gone in LargeFiles.Where(f => !File.Exists(f.File.FullPath)).ToList()) LargeFiles.Remove(gone);
        OnPropertyChanged(nameof(ShowFilterEmpty));
    }

    private static IEnumerable<string> RecycleBinFolders() =>
        DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d => Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin"));

    // ---------------------------------------------------------------- results

    private static Dictionary<string, long> FreeSpaceNow()
    {
        var free = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                // the type first: IsReady touches the drive, which can hang on a disconnected network drive
                if (drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady) free[drive.Name] = drive.TotalFreeSpace;
            }
            catch (IOException)
            {
            }
        }
        return free;
    }

    /// <summary>Free space gained on all drives since <paramref name="before"/>.</summary>
    private static long FreedSince(Dictionary<string, long> before)
    {
        long freed = 0;
        foreach (var (drive, now) in FreeSpaceNow())
        {
            if (before.TryGetValue(drive, out var then) && now > then) freed += now - then;
        }
        return freed;
    }

    private void ReportResult(long freed, long moved, IReadOnlyCollection<DeleteReport> reports, bool binFailed, bool binSkipped, bool cancelled)
    {
        var deleted = reports.Sum(r => r.ItemsDeleted);
        var blocked = reports.Sum(r => r.ItemsBlocked);
        var changedItems = reports.Sum(r => r.ItemsChanged);
        var problems = reports.SelectMany(r => r.Problems).ToList();
        var inUse = problems.Count(p => p.Kind == DeleteProblemKind.InUse);
        var denied = problems.Count(p => p.Kind == DeleteProblemKind.AccessDenied);
        var notMoved = problems.Count(p => p.Kind == DeleteProblemKind.NotMovedToRecycleBin);
        var other = problems.Count(p => p.Kind == DeleteProblemKind.Other);
        cancelled |= reports.Any(r => r.Cancelled);

        CrashReporter.Diagnostic($"delete: {deleted:N0} removed, {blocked:N0} blocked, {changedItems:N0} changed, {inUse:N0} in use, {denied:N0} denied, " +
                                 $"{notMoved:N0} not moved, {other:N0} other, freed {freed:N0}, moved {moved:N0}, cancelled {cancelled}" +
                                 string.Concat(problems.Take(10).Select(p => $"{Environment.NewLine}    {p.Kind} {p.BlockReason} {p.Path} {p.Detail}")));

        SetIsland(() => moved > 0
            ? Loc.I.F("IslandMoved", ByteFormat.Format(freed, Loc.I.Culture), ByteFormat.Format(moved, Loc.I.Culture))
            : Loc.I.F("IslandCleaned", ByteFormat.Format(freed, Loc.I.Culture)));
        SetStatus(() =>
        {
            var parts = new List<string> { Loc.I.FN("ResultDeleted", deleted, deleted) };
            if (inUse > 0) parts.Add(Loc.I.F("ResultInUse", inUse));
            if (denied > 0) parts.Add(Loc.I.F("ResultDenied", denied));
            if (blocked > 0) parts.Add(Loc.I.F("ResultBlocked", blocked));
            if (changedItems > 0) parts.Add(Loc.I.FN("ResultChanged", changedItems, changedItems));
            if (notMoved > 0) parts.Add(Loc.I.F("ResultNotMoved", notMoved));
            if (other > 0) parts.Add(Loc.I.FN("ResultOther", other, other));
            if (binFailed) parts.Add(Loc.I["ResultBinFailed"]);
            if (binSkipped) parts.Add(Loc.I["ResultBinSkipped"]);
            if (cancelled) parts.Add(Loc.I["ResultCancelled"]);
            var text = string.Join("  ·  ", parts);
            if (moved > 0) text += "  " + Loc.I["MovedHint"];
            return text;
        });
    }

    // ---------------------------------------------------------------- demo mode (screenshots)

    /// <summary>
    /// DISKTEMIZLEYICI_DEMO=en (or tr) shows made-up data instead of this computer's folders,
    /// so screenshots never contain anyone's real names, paths or files. Nothing can be deleted in this mode.
    /// </summary>
    public static string? DemoLanguage => Environment.GetEnvironmentVariable("DISKTEMIZLEYICI_DEMO");

    public bool IsDemo { get; } = !string.IsNullOrEmpty(DemoLanguage);

    public void LoadDemo()
    {
        var result = DemoData.CreateScan();
        _root = result.Root;
        Tree.Clear();
        var rootVm = new FolderItemViewModel(result.Root, null);
        Tree.Add(rootVm);
        rootVm.IsExpanded = true;
        var users = rootVm.Children.FirstOrDefault(c => c.Name == "Users");
        if (users is not null)
        {
            users.IsExpanded = true;
            var you = users.Children.FirstOrDefault(c => c.Name == "you");
            if (you is not null) you.IsExpanded = true;
        }

        _allLargeFiles.Clear();
        _allLargeFiles.AddRange(result.LargestFiles.Select(f => new LargeFileViewModel(f)));
        LargeFilesFilter = null;
        ApplyLargeFilesFilter();
        BuildFileTypes(result.FileTypes);
        HasScan = true;
        DeniedFolders = 0;

        Candidates.Clear();
        foreach (var c in DemoData.CreateCandidates(_rules)) Candidates.Add(new CandidateViewModel(c, UpdateSelectedTotal, IsAdmin));
        IsEvaluating = false;
        UpdateSelectedTotal();
        UpdateSuggestionsMessage();

        var size = result.Root.Size;
        var files = result.Root.FileCount;
        var seconds = result.Elapsed.TotalSeconds;
        var denied = result.InaccessibleDirectories;
        var shown = result.Root.FullPath;
        SetIsland(() => Loc.I.F("IslandDone", ByteFormat.Format(size, Loc.I.Culture), seconds));
        SetStatus(() => Loc.I.FN("ScanDone", files, shown, ByteFormat.Format(size, Loc.I.Culture), files, seconds, denied));
    }

    // ---------------------------------------------------------------- misc

    // Not in demo mode: the elevated copy would not get the demo setting and would scan the real disk.
    private bool CanRestartAsAdmin() => !IsBusy && !IsDemo;

    /// <summary>Restarts elevated and scans the same folder again. Not while something is being changed.</summary>
    [RelayCommand(CanExecute = nameof(CanRestartAsAdmin))]
    private void RestartAsAdmin()
    {
        if (!CanRestartAsAdmin()) return;
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
            // ArgumentList quotes correctly, also for "C:\" whose trailing backslash would escape a hand-written quote
            if (_root is not null) start.ArgumentList.Add(_root.FullPath);
            Process.Start(start);
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // the user said "No" to the UAC prompt
        }
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(LanguageCode));
        OnPropertyChanged(nameof(OtherUserWarning));
        OnPropertyChanged(nameof(DeniedButtonText));
        OnPropertyChanged(nameof(LargeFilesFilterText));
        LoadDrives();
        foreach (var t in Tree) t.Refresh();
        ApplyLargeFilesFilter();
        foreach (var c in Candidates) c.Refresh();
        foreach (var f in FileTypes) f.Refresh();
        UpdateSelectedTotal();
        UpdateSuggestionsMessage();
        IslandText = _islandRecipe();
        Status = _statusRecipe();
    }

    private static Task<T> RunOnStaThread<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
