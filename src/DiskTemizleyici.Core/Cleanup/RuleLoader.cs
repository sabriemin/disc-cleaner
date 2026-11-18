using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DiskTemizleyici.Core.Cleanup;

public sealed class RuleValidationException : Exception
{
    public RuleValidationException(string message) : base(message)
    {
    }
}

public static partial class RuleLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // A typo such as "keep_newest" must fail loudly instead of silently meaning keepNewest = 0.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    /// <summary>Loads the rules that ship inside the program. Throws if any of them is invalid.</summary>
    public static List<CleanupRule> LoadBuiltIn()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DiskTemizleyici.Core.rules.json")
            ?? throw new InvalidOperationException("Built-in rules.json is missing.");
        var rules = Parse(stream);
        var problems = rules.SelectMany(Validate).ToList();
        var duplicate = rules.Where(r => r is not null).GroupBy(r => r.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) problems.Add($"rule '{duplicate.Key}': the id is used more than once");
        if (problems.Count > 0) throw new RuleValidationException(string.Join(Environment.NewLine, problems));
        return rules;
    }

    /// <summary>
    /// Loads the built-in rules plus an optional <c>rules.json</c> placed next to the program. A local rule with the
    /// same id replaces the built-in one. Invalid local rules are skipped and described in <paramref name="problems"/>.
    /// </summary>
    public static List<CleanupRule> LoadAll(string? extraRulesFile, ICollection<string>? problems = null)
    {
        var rules = LoadBuiltIn();
        if (extraRulesFile is null || !File.Exists(extraRulesFile)) return rules;

        List<CleanupRule> extra;
        try
        {
            using var stream = File.OpenRead(extraRulesFile);
            extra = Parse(stream);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            problems?.Add($"{Path.GetFileName(extraRulesFile)}: {ex.Message}");
            return rules;
        }

        foreach (var rule in extra)
        {
            if (rule is null)
            {
                problems?.Add($"{Path.GetFileName(extraRulesFile)}: an entry is empty (null) and was skipped");
                continue;
            }
            var issues = Validate(rule);
            if (issues.Count > 0)
            {
                foreach (var issue in issues) problems?.Add(issue);
                continue;
            }
            rules.RemoveAll(r => r.Id == rule.Id);
            rules.Add(rule);
        }
        return rules;
    }

    public static List<CleanupRule> Parse(Stream json) =>
        JsonSerializer.Deserialize<List<CleanupRule>>(json, JsonOptions) ?? new();

    public static List<CleanupRule> Parse(string json) =>
        JsonSerializer.Deserialize<List<CleanupRule>>(json, JsonOptions) ?? new();

    /// <summary>Returns everything wrong with a rule; an empty list means it is fine.</summary>
    public static List<string> Validate(CleanupRule? rule)
    {
        var problems = new List<string>();
        if (rule is null)
        {
            problems.Add("an entry is empty (null)");
            return problems;
        }
        var id = string.IsNullOrWhiteSpace(rule.Id) ? "(no id)" : rule.Id;
        void Bad(string message) => problems.Add($"rule '{id}': {message}");

        if (string.IsNullOrWhiteSpace(rule.Id) || !IdPattern().IsMatch(rule.Id)) Bad("the id may only use a-z, 0-9 and '-'");
        if (rule.Name is null || !rule.Name.TryGetValue("en", out var name) || string.IsNullOrWhiteSpace(name)) Bad("needs an English name (\"en\")");
        if (rule.Description is null || !rule.Description.TryGetValue("en", out var description) || string.IsNullOrWhiteSpace(description))
            Bad("needs an English description (\"en\")");
        if (!Enum.IsDefined(rule.Mode)) Bad("unknown mode");
        if (!Enum.IsDefined(rule.Risk)) Bad("unknown risk");
        if (rule.KeepNewest < 0) Bad("keepNewest cannot be negative");
        if (rule.MinAgeDays < 0) Bad("minAgeDays cannot be negative");

        var paths = rule.Paths ?? new List<string>();
        if (rule.Mode == RuleMode.RecycleBin)
        {
            if (paths.Count > 0) Bad("RecycleBin rules take no paths");
        }
        else
        {
            if (paths.Count == 0) Bad("needs at least one path");
            foreach (var path in paths)
            {
                if (!IsValidRulePath(path))
                    Bad($"path '{path}' must start with a drive (C:\\) or an %ENV% variable and must not contain '..'");
            }
        }

        if (rule.Mode == RuleMode.Files)
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern)) Bad("Files rules need a pattern, e.g. \"AutoSave*\"");
            else if (!IsValidPattern(rule.Pattern)) Bad($"pattern '{rule.Pattern}' must be a file name (no folders, ':' or '..')");
        }
        else
        {
            if (rule.Pattern is not null) Bad("pattern is only used by Files rules");
            if (rule.KeepNewest != 0) Bad("keepNewest is only used by Files rules");
        }
        return problems;
    }

    private static bool IsValidRulePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path.Replace('/', '\\');
        if (p.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        var segments = p.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s == "." || s == "..")) return false;
        var first = segments[0];
        var isEnv = first.Length > 2 && first[0] == '%' && first[^1] == '%' && first.IndexOf('%', 1) == first.Length - 1;
        var isDrive = first.Length == 2 && char.IsAsciiLetter(first[0]) && first[1] == ':';
        return isEnv || isDrive;
    }

    private static bool IsValidPattern(string pattern) =>
        pattern.IndexOfAny(new[] { '\\', '/', ':' }) < 0 && !pattern.Contains("..", StringComparison.Ordinal);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex IdPattern();
}
