using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private readonly IShell _shell;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _progressTimer;
    private DiskScanner? _scanner;
    private CancellationTokenSource? _cts;
    private DirNode? _root;
    private List<CleanupRule> _rules = new();

    public MainViewModel(IShell shell, AppSettings settings)
    {
        _shell = shell;
        _settings = settings;
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _progressTimer.Tick += (_, _) => UpdateScanProgress();
        Loc.I.LanguageChanged += (_, _) => OnLanguageChanged();

        _selectedAccent = ThemeManager.FindAccent(settings.Accent);
        _isGlass = settings.Glass && ThemeManager.GlassSupported;
        _islandText = Loc.I["IslandReady"];
        LoadDrives();
        LoadRules();
    }

    public ObservableCollection<DriveViewModel> Drives { get; } = new();
    public ObservableCollection<FolderItemViewModel> Tree { get; } = new();
    public ObservableCollection<LargeFileViewModel> LargeFiles { get; } = new();
    public ObservableCollection<CandidateViewModel> Candidates { get; } = new();

    public bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>"0.2.0": release builds get it from the v0.2.0 tag (see .github/workflows/build.yml).</summary>
    public string AppVersion { get; } =
        (System.Reflection.Assembly.GetEntryAssembly()
            ?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0]; // the SDK appends "+<commit>"; keep the readable part

    // ---------------------------------------------------------------- state

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(ScanFolderCommand), nameof(CleanSelectedCommand), nameof(RefreshSuggestionsCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _hasScan;

    /// <summary>Nothing scanned yet and nothing running: show the welcome card.</summary>
    public bool ShowEmpty => !HasScan && !IsScanning;

    [ObservableProperty]
    private bool _isCleaning;

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

    /// <summary>0 = folders, 1 = large files, 2 = suggestions.</summary>
    [ObservableProperty]
    private int _selectedView;

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
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
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
            _rules = RuleLoader.LoadAll(Path.Combine(AppContext.BaseDirectory, "rules.json"));
        }
        catch (Exception ex)
        {
            _rules = RuleLoader.LoadBuiltIn();
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

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

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
        _cts = new CancellationTokenSource();
        _scanner = new DiskScanner();
        Tree.Clear();
        LargeFiles.Clear();
        HasScan = false;
        Status = "";
        ScanPath = path;
        UpdateScanProgress();
        _progressTimer.Start();
        try
        {
            var scanner = _scanner;
            var token = _cts.Token;
            var result = await Task.Run(() => scanner.Scan(path, token), token);
            _root = result.Root;

            var rootVm = new FolderItemViewModel(result.Root, null);
            Tree.Add(rootVm);
            rootVm.IsExpanded = true;
            foreach (var f in result.LargestFiles) LargeFiles.Add(new LargeFileViewModel(f));
            HasScan = true;

            IslandText = Loc.I.F("IslandDone", ByteFormat.Format(result.Root.Size, Loc.I.Culture), result.Elapsed.TotalSeconds);
            Status = Loc.I.F("ScanDone", result.Root.FullPath, ByteFormat.Format(result.Root.Size, Loc.I.Culture),
                result.Root.FileCount, result.Elapsed.TotalSeconds, result.InaccessibleDirectories);
        }
        catch (Exception ex) when (IsCancellation(ex))
        {
            IslandText = Loc.I["IslandCancelled"];
        }
        catch (Exception ex)
        {
            IslandText = Loc.I["IslandFailed"];
            Status = Loc.I.F("ScanFailed", ex.Message);
        }
        finally
        {
            _progressTimer.Stop();
            _scanner = null;
            IsScanning = false;
            IsBusy = false;
        }

        if (Candidates.Count == 0) await EvaluateSuggestionsAsync();
    }

    private void UpdateScanProgress()
    {
        var s = _scanner;
        if (s is null) return;
        ScanFilesText = s.FilesScanned.ToString("N0", Loc.I.Culture);
        ScanBytesText = ByteFormat.Format(s.BytesScanned, Loc.I.Culture);
        if (!string.IsNullOrEmpty(s.CurrentPath)) ScanPath = s.CurrentPath;
        IslandText = Loc.I.F("IslandScanning", s.FilesScanned, ScanBytesText);
    }

    private static bool IsCancellation(Exception ex) => ex switch
    {
        OperationCanceledException => true,
        AggregateException a => a.Flatten().InnerExceptions.All(e => e is OperationCanceledException),
        _ => false,
    };

    // ---------------------------------------------------------------- suggestions

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task RefreshSuggestionsAsync()
    {
        if (!IsDemo) return EvaluateSuggestionsAsync();
        LoadDemo();
        return Task.CompletedTask;
    }

    private async Task EvaluateSuggestionsAsync()
    {
        IsBusy = true;
        var previousIsland = IslandText;
        IslandText = Loc.I["IslandEvaluating"];
        try
        {
            var rules = _rules;
            var list = await Task.Run(() => RuleEvaluator.EvaluateAll(rules));
            Candidates.Clear();
            foreach (var c in list) Candidates.Add(new CandidateViewModel(c, UpdateSelectedTotal));
            SuggestionsMessage = Candidates.Count == 0 ? Loc.I["NoSuggestions"] : Loc.I["SuggestionsHint"];
            UpdateSelectedTotal();
        }
        catch (Exception ex)
        {
            _shell.Show(Loc.I["Error"], ex.Message, DialogKind.Error);
        }
        finally
        {
            IslandText = previousIsland;
            IsBusy = false;
        }
    }

    private void UpdateSelectedTotal()
    {
        var selected = Candidates.Where(c => c.IsSelected).Sum(c => c.Candidate.Size);
        var all = Candidates.Sum(c => c.Candidate.Size);
        SelectedTotalText = ByteFormat.Format(selected, Loc.I.Culture);
        ReclaimableText = all > 0 ? ByteFormat.Format(all, Loc.I.Culture) : "";
        CleanSelectedCommand.NotifyCanExecuteChanged();
    }

    private bool CanClean() => !IsBusy && Candidates.Any(c => c.IsSelected);

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanSelectedAsync()
    {
        var selected = Candidates.Where(c => c.IsSelected).Select(c => c.Candidate).ToList();
        if (selected.Count == 0 || IsDemo) return;

        var mode = PermanentDelete ? DeleteMode.Permanent : DeleteMode.RecycleBin;
        var totalSize = selected.Sum(c => c.Size);
        var itemCount = selected.Sum(c => c.Rule.Mode == RuleMode.RecycleBin ? c.FileCount : c.Items.Count);
        var lines = string.Join("\n", selected.Select(c =>
            $"•  {c.Rule.GetName(Loc.I.Language)}  —  {ByteFormat.Format(c.Size, Loc.I.Culture)}"));

        var message = lines + "\n\n" + Loc.I.F(mode == DeleteMode.Permanent ? "ConfirmPermanent" : "ConfirmRecycle",
            itemCount, ByteFormat.Format(totalSize, Loc.I.Culture));
        var bin = selected.FirstOrDefault(c => c.Rule.Mode == RuleMode.RecycleBin);
        if (bin is not null) message += "\n\n" + Loc.I.F("ConfirmEmptyBin", ByteFormat.Format(bin.Size, Loc.I.Culture));

        var dangerous = mode == DeleteMode.Permanent || bin is not null;
        if (!_shell.Confirm(Loc.I["ConfirmCleanTitle"], message, dangerous ? DialogKind.Danger : DialogKind.Question,
                Loc.I[dangerous ? "DialogDelete" : "DialogContinue"]))
            return;

        IsBusy = true;
        IsCleaning = true;
        ProgressValue = 0;
        long deleted = 0, skipped = 0;
        var freeBefore = SelectedDrive?.FreeBytes ?? 0;
        try
        {
            var total = Math.Max(1, selected.Sum(c => c.Items.Count));
            var doneBefore = 0;
            var owner = _shell.OwnerHandle;
            foreach (var c in selected)
            {
                if (c.Rule.Mode == RuleMode.RecycleBin)
                {
                    var ok = await RunOnStaThread(RecycleBin.Empty);
                    if (ok) deleted += c.FileCount; else skipped += c.FileCount;
                    continue;
                }

                var offset = doneBefore;
                var progress = new Progress<int>(n =>
                {
                    ProgressValue = 100.0 * (offset + n) / total;
                    IslandText = Loc.I.F("IslandCleaning", ProgressValue);
                });
                var items = c.Items;
                // The Recycle Bin is a shell operation and must run on an STA thread.
                var report = await RunOnStaThread(() => Deleter.Delete(items, mode, progress, ownerWindow: owner));
                deleted += report.ItemsDeleted;
                skipped += report.ItemsFailed + report.ItemsBlocked;
                doneBefore += items.Count;
            }
        }
        catch (Exception ex)
        {
            _shell.Show(Loc.I["Error"], ex.Message, DialogKind.Error);
        }
        finally
        {
            IsCleaning = false;
            IsBusy = false;
            ProgressValue = 0;
        }

        LoadDrives();
        var freed = Math.Max(0, (SelectedDrive?.FreeBytes ?? 0) - freeBefore);
        IslandText = Loc.I.F("IslandCleaned", ByteFormat.Format(freed, Loc.I.Culture));
        Status = Loc.I.F("CleanDone", deleted, skipped) + (_root is not null ? "  " + Loc.I["TreeStale"] : "");
        await EvaluateSuggestionsAsync();
    }

    // ---------------------------------------------------------------- tree / file actions

    [RelayCommand]
    private void OpenInExplorer(object? item)
    {
        var path = PathOf(item);
        if (path is null) return;
        var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    [RelayCommand]
    private void CopyPath(object? item)
    {
        var path = PathOf(item);
        if (path is not null) Clipboard.SetText(path);
    }

    [RelayCommand]
    private Task MoveToRecycle(object? item) => DeleteItemAsync(item, DeleteMode.RecycleBin);

    [RelayCommand]
    private Task DeletePermanent(object? item) => DeleteItemAsync(item, DeleteMode.Permanent);

    private async Task DeleteItemAsync(object? item, DeleteMode mode)
    {
        if (IsBusy || IsDemo) return;
        var path = PathOf(item);
        if (path is null || item is FolderItemViewModel { CanDelete: false }) return;

        if (!SafetyGuard.CanDelete(path, out _))
        {
            _shell.Show(Loc.I["BlockedTitle"], Loc.I.F("Blocked", path), DialogKind.Error);
            return;
        }

        var (size, count) = item switch
        {
            FolderItemViewModel f => (f.Size, Math.Max(1, f.Node?.FileCount ?? 1)),
            LargeFileViewModel l => (l.Size, 1L),
            _ => (0L, 1L),
        };
        var permanent = mode == DeleteMode.Permanent;
        var message = path + "\n\n" + Loc.I.F(permanent ? "ConfirmPermanent" : "ConfirmRecycle",
            count, ByteFormat.Format(size, Loc.I.Culture));
        if (!_shell.Confirm(Loc.I[permanent ? "DeletePermanentTitle" : "MoveToRecycleTitle"], message,
                permanent ? DialogKind.Danger : DialogKind.Question, Loc.I[permanent ? "DialogDelete" : "DialogContinue"]))
            return;

        IsBusy = true;
        try
        {
            var owner = _shell.OwnerHandle;
            var report = await RunOnStaThread(() => Deleter.Delete(new[] { path }, mode, ownerWindow: owner));
            if (report.ItemsDeleted > 0)
            {
                switch (item)
                {
                    case FolderItemViewModel f:
                        f.RemoveFromTree();
                        break;
                    case LargeFileViewModel l:
                        LargeFiles.Remove(l);
                        _root?.Find(l.Folder)?.SubtractFreed(l.Size, 1);
                        foreach (var t in Tree) t.Refresh();
                        break;
                }
                IslandText = Loc.I.F("IslandCleaned", ByteFormat.Format(size, Loc.I.Culture));
            }
            else if (report.Errors.Count > 0)
            {
                _shell.Show(Loc.I["Error"], string.Join("\n", report.Errors.Take(10)), DialogKind.Error);
            }
        }
        finally
        {
            IsBusy = false;
        }
        LoadDrives();
    }

    private static string? PathOf(object? item) => item switch
    {
        FolderItemViewModel { IsFilesRow: true } f => f.Parent?.Node?.FullPath,
        FolderItemViewModel f => f.Node?.FullPath,
        LargeFileViewModel l => l.File.FullPath,
        _ => null,
    };

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

        LargeFiles.Clear();
        foreach (var f in result.LargestFiles) LargeFiles.Add(new LargeFileViewModel(f));
        HasScan = true;

        Candidates.Clear();
        foreach (var c in DemoData.CreateCandidates(_rules)) Candidates.Add(new CandidateViewModel(c, UpdateSelectedTotal));
        SuggestionsMessage = Loc.I["SuggestionsHint"];
        UpdateSelectedTotal();

        IslandText = Loc.I.F("IslandDone", ByteFormat.Format(result.Root.Size, Loc.I.Culture), result.Elapsed.TotalSeconds);
        Status = Loc.I.F("ScanDone", result.Root.FullPath, ByteFormat.Format(result.Root.Size, Loc.I.Culture),
            result.Root.FileCount, result.Elapsed.TotalSeconds, result.InaccessibleDirectories);
    }

    // ---------------------------------------------------------------- misc

    [RelayCommand]
    private void RestartAsAdmin()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
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
        LoadDrives();
        foreach (var t in Tree) t.Refresh();
        var files = LargeFiles.ToList();
        LargeFiles.Clear();
        foreach (var f in files) LargeFiles.Add(f);
        foreach (var c in Candidates) c.Refresh();
        UpdateSelectedTotal();
        if (Candidates.Count > 0) SuggestionsMessage = Loc.I["SuggestionsHint"];
        if (!IsBusy)
        {
            IslandText = Loc.I["IslandReady"];
            Status = "";
        }
        // accent names are localised too
        var accent = SelectedAccent;
        OnPropertyChanged(nameof(Accents));
        SelectedAccent = accent;
    }

    private static Task<T> RunOnStaThread<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
