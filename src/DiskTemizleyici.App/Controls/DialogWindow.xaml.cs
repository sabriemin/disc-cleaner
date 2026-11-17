using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using DiskTemizleyici.App.Localization;
using DiskTemizleyici.App.ViewModels;

namespace DiskTemizleyici.App.Controls;

/// <summary>Dark, rounded replacement for MessageBox.</summary>
public partial class DialogWindow : Window
{
    private DialogWindow(string title, string message, DialogKind kind, string? confirmText, bool showCancel)
    {
        InitializeComponent();
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

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
