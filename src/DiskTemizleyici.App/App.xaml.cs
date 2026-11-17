using System.Windows;
using System.Windows.Threading;
using DiskTemizleyici.App.Localization;
using DiskTemizleyici.App.Services;

namespace DiskTemizleyici.App;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;

        Settings = AppSettings.Load();
        if (!string.IsNullOrEmpty(Settings.Language)) Loc.I.Language = Settings.Language;
        if (ViewModels.MainViewModel.DemoLanguage is { Length: > 0 } demo) Loc.I.Language = demo;
        ThemeManager.ApplyAccent(ThemeManager.FindAccent(Settings.Accent));

        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "DiskTemizleyici", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
