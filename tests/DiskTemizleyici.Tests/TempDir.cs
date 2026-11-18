namespace DiskTemizleyici.Tests;

/// <summary>A throw-away folder under %TEMP% that is removed after the test.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiskTemizleyiciTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        // %TEMP% can be an 8.3 short path (C:\Users\RUNNER~1\...); the code under test reports long names
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    public string File(string relative, int bytes, DateTime? lastWriteUtc = null)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, new byte[bytes]);
        if (lastWriteUtc is not null)
        {
            // the cleaner treats a file as new if it was written OR created recently, so set both
            System.IO.File.SetCreationTimeUtc(full, lastWriteUtc.Value);
            System.IO.File.SetLastWriteTimeUtc(full, lastWriteUtc.Value);
        }
        return full;
    }

    public string Dir(string relative)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Creates a directory junction at <paramref name="linkRelative"/> pointing to <paramref name="target"/>.</summary>
    public string Junction(string linkRelative, string target)
    {
        var link = System.IO.Path.Combine(Path, linkRelative);
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        p.WaitForExit();
        if (!Directory.Exists(link)) throw new InvalidOperationException("could not create a junction for the test");
        return link;
    }

    public void Dispose()
    {
        try
        {
            RemoveLinks(Path);
            Directory.Delete(Path, true);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>
    /// Removes junctions and symbolic links below <paramref name="folder"/> without ever entering them.
    /// (A recursive EnumerateDirectories would follow a junction into, say, C:\Windows.)
    /// </summary>
    internal static void RemoveLinks(string folder)
    {
        var oneLevel = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = true };
        foreach (var d in Directory.EnumerateDirectories(folder, "*", oneLevel).ToList())
        {
            var attrs = System.IO.File.GetAttributes(d);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                System.IO.File.SetAttributes(d, attrs & ~(FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly));
                Directory.Delete(d, recursive: false); // removes the link only
            }
            else
            {
                RemoveLinks(d);
            }
        }
    }
}
