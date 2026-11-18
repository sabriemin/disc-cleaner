namespace DiskTemizleyici.Core.Cleanup;

public static class PathExpander
{
    /// <summary>
    /// Expands environment variables and <c>*</c> path segments, returning only folders that exist.
    /// Relative paths, network paths and <c>..</c> segments are refused, so a rule can never point outside the
    /// folder it names.
    /// </summary>
    public static IEnumerable<string> Expand(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return Array.Empty<string>();

        var expanded = Environment.ExpandEnvironmentVariables(pattern.Replace('/', '\\'));
        if (expanded.Contains('%')) return Array.Empty<string>(); // unknown variable
        if (expanded.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(expanded)) return Array.Empty<string>();

        var parts = expanded.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p == "." || p == "..")) return Array.Empty<string>();

        IEnumerable<string> current = new[] { parts[0] + Path.DirectorySeparatorChar };
        foreach (var part in parts.Skip(1))
        {
            var p = part;
            current = current.SelectMany(dir => Step(dir, p)).ToList();
        }

        return current
            .Where(Directory.Exists)
            .Select(LongForm)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The full path with 8.3 short names expanded. %TEMP% is often stored as C:\Users\JOHNSM~1\..., while the
    /// scanner and everything else use the long names.
    /// </summary>
    private static string LongForm(string folder)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); // expands "~" names of existing folders
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Path.TrimEndingDirectorySeparator(folder);
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
