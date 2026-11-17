namespace DiskTemizleyici.Core.Cleanup;

public static class PathExpander
{
    /// <summary>
    /// Expands environment variables and <c>*</c> path segments, returning only folders that exist.
    /// </summary>
    public static IEnumerable<string> Expand(string pattern)
    {
        var expanded = Environment.ExpandEnvironmentVariables(pattern);
        if (expanded.Contains('%')) yield break; // unknown variable

        var parts = expanded.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) yield break;

        IEnumerable<string> current = new[] { parts[0] + Path.DirectorySeparatorChar };
        foreach (var part in parts.Skip(1))
        {
            var p = part;
            current = current.SelectMany(dir => Step(dir, p)).ToList();
        }

        foreach (var dir in current.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(dir)) yield return Path.TrimEndingDirectorySeparator(dir);
        }
    }

    private static IEnumerable<string> Step(string dir, string part)
    {
        if (!part.Contains('*') && !part.Contains('?'))
        {
            var next = Path.Combine(dir, part);
            return Directory.Exists(next) ? new[] { next } : Array.Empty<string>();
        }
        try
        {
            return Directory.EnumerateDirectories(dir, part, new EnumerationOptions { IgnoreInaccessible = true }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
