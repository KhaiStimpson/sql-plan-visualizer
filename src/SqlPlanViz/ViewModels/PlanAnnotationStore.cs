using System.Text.Json;

namespace SqlPlanViz.ViewModels;

/// <summary>Persists lightweight node notes in a JSON sidecar beside the source plan.</summary>
public sealed class PlanAnnotationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public IReadOnlyDictionary<int, string> Load(string? planPath)
    {
        if (string.IsNullOrWhiteSpace(planPath) || !File.Exists(SidecarPath(planPath)))
        {
            return new Dictionary<int, string>();
        }

        try
        {
            var json = File.ReadAllText(SidecarPath(planPath));
            return JsonSerializer.Deserialize<Dictionary<int, string>>(json, JsonOptions)
                   ?? new Dictionary<int, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<int, string>();
        }

    }

    public void Save(string planPath, IReadOnlyDictionary<int, string> annotations)
    {
        var retained = annotations
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Trim());
        File.WriteAllText(SidecarPath(planPath), JsonSerializer.Serialize(retained, JsonOptions));
    }

    public static string SidecarPath(string planPath) => planPath + ".annotations.json";
}
