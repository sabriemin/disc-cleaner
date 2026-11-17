using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DiskTemizleyici.App.Services;

/// <summary>One selectable accent colour.</summary>
public sealed class AccentOption
{
    public AccentOption(string id, string nameTr, string nameEn, Color light, Color main, bool darkText)
    {
        Id = id;
        NameTr = nameTr;
        NameEn = nameEn;
        Light = light;
        Main = main;
        DarkText = darkText;
        Brush = new LinearGradientBrush(light, main, 45);
        Brush.Freeze();
    }

    public string Id { get; }
    public string NameTr { get; }
    public string NameEn { get; }
    public Color Light { get; }
    public Color Main { get; }

    /// <summary>True when text on this accent should be dark (bright accents).</summary>
    public bool DarkText { get; }

    public LinearGradientBrush Brush { get; }

    public string Name => Localization.Loc.I.Language == "tr" ? NameTr : NameEn;
}

/// <summary>
/// Swaps the theme brushes at runtime (accent colour, solid or glass look) and talks to the Windows
/// compositor (DWM) for the dark frame, rounded corners and the acrylic "glass" backdrop.
/// </summary>
public static class ThemeManager
{
    public static IReadOnlyList<AccentOption> Accents { get; } = new[]
    {
        new AccentOption("honey", "Bal", "Honey", Rgb(0xFFC857), Rgb(0xFF8A1F), true),
        new AccentOption("mint", "Nane", "Mint", Rgb(0x7CF0A6), Rgb(0x26C26B), true),
        new AccentOption("sky", "Gök", "Sky", Rgb(0x6CB8FF), Rgb(0x0A6CFF), false),
        new AccentOption("rose", "Gül", "Rose", Rgb(0xFF8FA3), Rgb(0xFF375F), false),
        new AccentOption("lavender", "Lavanta", "Lavender", Rgb(0xD7A6FF), Rgb(0x9B4DFF), false),
        new AccentOption("snow", "Kar", "Snow", Rgb(0xFFFFFF), Rgb(0xD9D9E0), true),
    };

    /// <summary>The acrylic backdrop needs Windows 11 22H2 (build 22621) or newer.</summary>
    public static bool GlassSupported { get; } = Environment.OSVersion.Version.Build >= 22621;

    public static AccentOption FindAccent(string? id) => Accents.FirstOrDefault(a => a.Id == id) ?? Accents[0];

    public static void ApplyAccent(AccentOption accent)
    {
        var res = Application.Current.Resources;
        res["AccentBrush"] = Frozen(new SolidColorBrush(accent.Main));
        res["AccentSoftBrush"] = Frozen(new SolidColorBrush(WithAlpha(accent.Main, 0x2E)));
        res["AccentForegroundBrush"] = Frozen(new SolidColorBrush(accent.DarkText ? Rgb(0x111111) : Colors.White));
        res["AccentGradientBrush"] = Frozen(new LinearGradientBrush(accent.Light, accent.Main, new Point(0, 0), new Point(1, 1)));

        var glow = new RadialGradientBrush
        {
            Center = new Point(0.85, 0),
            GradientOrigin = new Point(0.85, 0),
            RadiusX = 0.7,
            RadiusY = 0.9,
        };
        glow.GradientStops.Add(new GradientStop(WithAlpha(accent.Main, 0x40), 0));
        glow.GradientStops.Add(new GradientStop(WithAlpha(accent.Main, 0x00), 1));
        res["GlowBrush"] = Frozen(glow);
    }

    /// <summary>Solid: opaque dark surfaces. Glass: translucent surfaces over the Windows acrylic backdrop.</summary>
    public static void ApplyStyle(Window window, bool glass)
    {
        glass &= GlassSupported;
        var res = Application.Current.Resources;
        res["WindowBackgroundBrush"] = Frozen(new SolidColorBrush(glass ? Color.FromArgb(0x70, 0x08, 0x08, 0x0A) : Rgb(0x0B0B0D)));
        res["SurfaceBrush"] = Frozen(new SolidColorBrush(glass ? Color.FromArgb(0xA6, 0x16, 0x16, 0x1A) : Rgb(0x141417)));
        res["SurfaceAltBrush"] = Frozen(new SolidColorBrush(glass ? Color.FromArgb(0xB3, 0x22, 0x22, 0x27) : Rgb(0x1D1D21)));

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
            target.BackgroundColor = glass ? Colors.Transparent : Rgb(0x0B0B0D);

        SetInt(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        SetInt(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
        SetInt(hwnd, DWMWA_BORDER_COLOR, 0x002E2A2A); // COLORREF 0x00BBGGRR: a dark grey hairline
        if (GlassSupported)
            SetInt(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, glass ? DWMSBT_TRANSIENTWINDOW : DWMSBT_NONE);

        // With the frame extended into the client area, Windows would draw its own caption buttons
        // behind transparent content. The window draws its own buttons, so hide the system ones in glass mode.
        var style = GetWindowLong(hwnd, GWL_STYLE);
        var newStyle = glass ? style & ~WS_SYSMENU : style | WS_SYSMENU;
        if (newStyle != style)
        {
            SetWindowLong(hwnd, GWL_STYLE, newStyle);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static void SetInt(IntPtr hwnd, int attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_NONE = 1;
    private const int DWMSBT_TRANSIENTWINDOW = 3; // acrylic

    private const int GWL_STYLE = -16;
    private const int WS_SYSMENU = 0x00080000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
