using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using DiskTemizleyici.App.Localization;
using DiskTemizleyici.App.Services;
using DiskTemizleyici.App.ViewModels;

namespace DiskTemizleyici.App.Controls;

/// <summary>Dark, rounded replacement for MessageBox.</summary>
public partial class DialogWindow : Window
{
    private DialogWindow(string title, string message, DialogKind kind, string? confirmText, bool showCancel)
    {
        InitializeComponent();
        Title = title; // read by screen readers and shown in Alt+Tab
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = confirmText ?? Loc.I["DialogOk"];
        CancelButton.Content = Loc.I["DialogCancel"];
        CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;

        switch (kind)
        {
            case DialogKind.Danger:
                IconGlyph.Text = ""; // Warning
                IconGlyph.SetResourceReference(ForegroundProperty, "DangerBrush");
                IconTile.SetResourceReference(BackgroundProperty, "DangerSoftBrush");
                OkButton.Style = (Style)FindResource("DangerButton");
                break;
            case DialogKind.Error:
                IconGlyph.Text = ""; // Error badge
                IconGlyph.SetResourceReference(ForegroundProperty, "DangerBrush");
                IconTile.SetResourceReference(BackgroundProperty, "DangerSoftBrush");
                break;
            case DialogKind.Question:
                IconGlyph.Text = ""; // Help
                break;
        }

        // Dangerous actions: the safe answer has the keyboard focus.
        Loaded += (_, _) =>
        {
            (kind == DialogKind.Danger && showCancel ? CancelButton : OkButton).Focus();
            var ease = (IEasingFunction)FindResource("SpringOut");
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            Scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
            Scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        };
    }

    public static bool Confirm(Window owner, string title, string message, DialogKind kind, string confirmText)
    {
        var dialog = new DialogWindow(title, message, kind, confirmText, showCancel: true) { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    public static void Show(Window owner, string title, string message, DialogKind kind)
    {
        var dialog = new DialogWindow(title, message, kind, null, showCancel: false) { Owner = owner };
        dialog.ShowDialog();
    }

    /// <summary>
    /// "A report was saved, nothing was sent" with Copy / Open folder / Report on GitHub; without a saved report
    /// (a full disk, for example) it says so and offers only the GitHub link.
    /// </summary>
    public static void ShowCrash(Window? owner, string? reportPath, string message)
    {
        var text = Loc.I.F(reportPath is null ? "CrashTextNotSaved" : "CrashText", message);
        var dialog = new DialogWindow(Loc.I["CrashTitle"], text, DialogKind.Error, Loc.I["DialogClose"], showCancel: false)
        {
            Width = 680, // room for four buttons
        };
        if (owner is { IsLoaded: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (reportPath is not null)
        {
            dialog.AddButton(Loc.I["CrashCopy"], () =>
            {
                try
                {
                    Clipboard.SetText(File.ReadAllText(reportPath));
                }
                catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.ExternalException)
                {
                }
            });
            dialog.AddButton(Loc.I["CrashOpenFolder"], () =>
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{reportPath}\"") { UseShellExecute = true }));
        }
        dialog.AddButton(Loc.I["CrashReportIssue"], () =>
            Process.Start(new ProcessStartInfo(CrashReporter.IssueUrl()) { UseShellExecute = true }));
        dialog.ShowDialog();
    }

    private void AddButton(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource("GhostButton"),
            Margin = new Thickness(0, 0, 10, 0),
        };
        button.Click += (_, _) => action();
        ButtonRow.Children.Insert(0, button);
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
