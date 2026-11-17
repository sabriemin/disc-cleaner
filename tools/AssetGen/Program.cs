using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiskTemizleyici.App.Controls;

namespace AssetGen;

/// <summary>Writes src/DiskTemizleyici.App/Assets/app.ico and docs/logo.png from the XAML logo.</summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var repo = args.Length > 0 ? args[0] : FindRepoRoot();
        _ = new Application(); // registers the pack:// scheme used by the XAML logo

        var icoSizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var images = icoSizes.Select(s => (Size: s, Png: Render(s))).ToList();

        var icoPath = Path.Combine(repo, "src", "DiskTemizleyici.App", "Assets", "app.ico");
        Directory.CreateDirectory(Path.GetDirectoryName(icoPath)!);
        WriteIco(icoPath, images);
        Console.WriteLine($"wrote {icoPath}");

        var logoPath = Path.Combine(repo, "docs", "logo.png");
        Directory.CreateDirectory(Path.GetDirectoryName(logoPath)!);
        File.WriteAllBytes(logoPath, Render(256));
        Console.WriteLine($"wrote {logoPath}");
        return 0;
    }

    private static byte[] Render(int size)
    {
        var logo = new LogoMark { Width = size, Height = size };
        logo.Measure(new Size(size, size));
        logo.Arrange(new Rect(0, 0, size, size));
        logo.UpdateLayout();

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(logo);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>An .ico file whose entries are PNG images (supported since Windows Vista).</summary>
    private static void WriteIco(string path, IReadOnlyList<(int Size, byte[] Png)> images)
    {
        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        w.Write((ushort)0);            // reserved
        w.Write((ushort)1);            // type: icon
        w.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);          // palette
            w.Write((byte)0);          // reserved
            w.Write((ushort)1);        // planes
            w.Write((ushort)32);       // bits per pixel
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DiskTemizleyici.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Run from inside the repository.");
    }
}
