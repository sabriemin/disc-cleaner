namespace DiskTemizleyici.Tests;

/// <summary>A throw-away folder under %TEMP% that is removed after the test.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiskTemizleyiciTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relative, int bytes, DateTime? lastWriteUtc = null)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, new byte[bytes]);
        if (lastWriteUtc is not null) System.IO.File.SetLastWriteTimeUtc(full, lastWriteUtc.Value);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* best effort */ }
    }
}
