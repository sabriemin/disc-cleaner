using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DiskTemizleyici.App.Controls;
using DiskTemizleyici.App.Services;
using DiskTemizleyici.App.ViewModels;
using Microsoft.Win32;

namespace DiskTemizleyici.App;

public partial class MainWindow : Window, IShell
{
    private readonly MainViewModel _vm;
    private double _islandTarget;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(this, App.Settings);
        DataContext = _vm;
        _vm.PropertyChanged += OnViewModelChanged;

        SourceInitialized += (_, _) => ThemeManager.ApplyStyle(this, _vm.IsGlass);
        StateChanged += (_, _) => OnStateChanged();
        Loaded += (_, _) => ResizeIsland(animate: false);

        // "DiskTemizleyici.exe D:\Games" starts scanning that folder right away.
        var args = Environment.GetCommandLineArgs();
        if (_vm.IsDemo)
        {
            Loaded += (_, _) => _vm.LoadDemo();
        }
        else if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
        {
            var folder = args[1];
            if (Directory.Exists(folder))
                Loaded += async (_, _) => await _vm.StartScanAsync(folder);
            else
                Loaded += (_, _) => Show(Localization.Loc.I["Error"], Localization.Loc.I.F("PathNotFound", folder), DialogKind.Error);
        }
    }

    /// <summary>The keyboard shortcuts act on the selected folder.</summary>
    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _vm.SelectedFolder = e.NewValue as FolderItemViewModel;

    /// <summary>
    /// A right-click selects the row first (a TreeView only does that for the left button), so the context menu and
    /// the shortcuts it lists (Del, Enter, Ctrl+C, F5) act on the same folder.
    /// </summary>
    private void OnTreeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null;
             d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is not TreeViewItem row) continue;
            row.IsSelected = true;
            row.Focus();
            return;
        }
    }

    /// <summary>Esc closes the settings popover, and only that: it must not also stop a running scan or clean-up.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SettingsPopup.IsOpen)
        {
            SettingsToggle.IsChecked = false; // the popup's IsOpen follows the toggle
            SettingsToggle.Focus();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    // ------------------------------------------------------------------ IShell

    public IntPtr OwnerHandle => Dispatcher.Invoke(() => new WindowInteropHelper(this).Handle);

    public bool Confirm(string title, string message, DialogKind kind, string confirmText) =>
        DialogWindow.Confirm(this, title, message, kind, confirmText);

    public void Show(string title, string message, DialogKind kind) =>
        DialogWindow.Show(this, title, message, kind);

    public string? PickFolder()
    {
        var dialog = new OpenFolderDialog { Multiselect = false };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    public void ApplyGlass(bool glass) => ThemeManager.ApplyStyle(this, glass);

    // ------------------------------------------------------------------ chrome

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnStateChanged()
    {
        // A maximised WindowChrome window hangs over the screen edge by the resize border.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    // ------------------------------------------------------------------ motion

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IslandText):
            case nameof(MainViewModel.IsBusy):
            case nameof(MainViewModel.IsCleaning):
                ResizeIsland(animate: true);
                break;
            case nameof(MainViewModel.SelectedView):
                AnimateViewIn(_vm.SelectedView switch { 1 => FilesView, 2 => SuggestionsView, 3 => TypesView, _ => FoldersView });
                break;
        }
    }

    /// <summary>The island grows and shrinks with a little spring, like a notch that reacts.</summary>
    private void ResizeIsland(bool animate)
    {
        var text = new FormattedText(_vm.IslandText ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(IslandLabel.FontFamily, IslandLabel.FontStyle, IslandLabel.FontWeight, IslandLabel.FontStretch),
            IslandLabel.FontSize, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var width = text.WidthIncludingTrailingWhitespace + 36 /* padding */ + 18 /* dot */ + 8;
        if (_vm.IsCleaning) width += 76;
        if (_vm.IsBusy) width = Math.Max(width, 420);
        width = Math.Clamp(Math.Ceiling(width), 200, 680);

        if (Math.Abs(width - _islandTarget) < 6) return;
        _islandTarget = width;

        if (!animate)
        {
            Island.Width = width;
            return;
        }
        Island.BeginAnimation(WidthProperty, new DoubleAnimation(width, TimeSpan.FromMilliseconds(520))
        {
            EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 7 },
        });
    }

    private static void AnimateViewIn(FrameworkElement view)
    {
        view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        if (view.RenderTransform is TranslateTransform t && !t.IsFrozen)
        {
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 },
            });
        }
        else
        {
            var fresh = new TranslateTransform();
            view.RenderTransform = fresh;
            fresh.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 },
            });
        }
    }
}
