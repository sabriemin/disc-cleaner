using System.Text.Json;

namespace DiskTemizleyici.App.Services;

/// <summary>User preferences, stored in %LOCALAPPDATA%\DiskTemizleyici\settings.json.</summary>
public sealed class AppSettings
{
    public string? Language { get; set; }
    public string Accent { get; set; } = "honey";
    public bool Glass { get; set; }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskTemizleyici", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // a broken settings file must never stop the app: fall back to defaults
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // write next to it first and then swap, so a crash mid-write never leaves a half-written file
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
