using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskTemizleyici.Core.Cleanup;

public static class RuleLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Loads the rules that ship inside the program.</summary>
    public static List<CleanupRule> LoadBuiltIn()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DiskTemizleyici.Core.rules.json")
            ?? throw new InvalidOperationException("Built-in rules.json is missing.");
        return Parse(stream);
    }

    /// <summary>
    /// Loads the built-in rules plus an optional <c>rules.json</c> placed next to the program.
    /// A local rule with the same id replaces the built-in one.
    /// </summary>
    public static List<CleanupRule> LoadAll(string? extraRulesFile)
    {
        var rules = LoadBuiltIn();
        if (extraRulesFile is null || !File.Exists(extraRulesFile)) return rules;

        using var stream = File.OpenRead(extraRulesFile);
        foreach (var extra in Parse(stream))
        {
            rules.RemoveAll(r => r.Id == extra.Id);
            rules.Add(extra);
        }
        return rules;
    }

    public static List<CleanupRule> Parse(Stream json) =>
        JsonSerializer.Deserialize<List<CleanupRule>>(json, JsonOptions) ?? new();

    public static List<CleanupRule> Parse(string json) =>
        JsonSerializer.Deserialize<List<CleanupRule>>(json, JsonOptions) ?? new();
}
