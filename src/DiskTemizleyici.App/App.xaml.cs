using System.Windows;
using System.Windows.Threading;
using DiskTemizleyici.App.Controls;
using DiskTemizleyici.App.Localization;
using DiskTemizleyici.App.Services;
using DiskTemizleyici.App.ViewModels;

namespace DiskTemizleyici.App;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) CrashReporter.Write(ex, "background thread");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashReporter.Write(args.Exception, "background task");
            args.SetObserved();
        };

        Settings = AppSettings.Load();
        if (!string.IsNullOrEmpty(Settings.Language)) Loc.I.Language = Settings.Language;
        if (MainViewModel.DemoLanguage is { Length: > 0 } demo) Loc.I.Language = demo;
        ThemeManager.ApplyAccent(ThemeManager.FindAccent(Settings.Accent));

        base.OnStartup(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        // Whatever was running (a clean-up in particular) stops before anything else happens.
        if (MainWindow?.DataContext is MainViewModel vm) vm.CancelCommand.Execute(null);

        var report = CrashReporter.Write(e.Exception, "user interface");
        try
        {
            DialogWindow.ShowCrash(MainWindow, report, e.Exception.Message);
        }
        catch (Exception dialogError) when (dialogError is InvalidOperationException or System.Windows.Markup.XamlParseException)
        {
            MessageBox.Show(Loc.I.F(report is null ? "CrashTextNotSaved" : "CrashText", e.Exception.Message), Loc.I["CrashTitle"],
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
