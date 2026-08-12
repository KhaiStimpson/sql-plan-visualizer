using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlPlanViz.ViewModels;

public enum FixTriageState { Untried, Tried, DidNotHelp, Fixed }

public sealed class FindingTriageStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public Dictionary<string, FixTriageState> Load(string? planPath)
    {
        if (string.IsNullOrWhiteSpace(planPath) || !File.Exists(PathFor(planPath)))
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, FixTriageState>>(File.ReadAllText(PathFor(planPath)), Options)
                   ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Save(string planPath, IReadOnlyDictionary<string, FixTriageState> states)
    {
        var retained = states.Where(pair => pair.Value != FixTriageState.Untried).ToDictionary();
        File.WriteAllText(PathFor(planPath), JsonSerializer.Serialize(retained, Options));
    }

    public static string PathFor(string planPath) => planPath + ".triage.json";
}
