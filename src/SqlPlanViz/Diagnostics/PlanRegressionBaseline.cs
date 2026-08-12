using System.Text.Json;
using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics;

public sealed record PlanRegressionBaseline
{
    public string Fingerprint { get; init; } = string.Empty;
    public double? DurationMs { get; init; }
    public double DurationToleranceFraction { get; init; } = 0.20;
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
}

public sealed record RegressionCheckResult(bool Success, string Message);

public static class PlanRegressionBaselineStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Save(PlanStatement statement, string path)
    {
        var baseline = new PlanRegressionBaseline
        {
            Fingerprint = statement.Fingerprint,
            DurationMs = statement.Summary.QueryElapsedMs,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(baseline, Options));
    }

    public static RegressionCheckResult Check(PlanStatement statement, string path)
    {
        if (!File.Exists(path))
        {
            return new RegressionCheckResult(false, $"Baseline not found: {path}");
        }

        PlanRegressionBaseline? baseline;
        try
        {
            baseline = JsonSerializer.Deserialize<PlanRegressionBaseline>(File.ReadAllText(path), Options);
        }
        catch (JsonException ex)
        {
            return new RegressionCheckResult(false, $"Baseline is invalid JSON: {ex.Message}");
        }

        if (baseline is null || string.IsNullOrWhiteSpace(baseline.Fingerprint))
        {
            return new RegressionCheckResult(false, "Baseline does not contain a fingerprint.");
        }

        if (!string.Equals(baseline.Fingerprint, statement.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return new RegressionCheckResult(false, $"Plan shape changed: expected {baseline.Fingerprint[..8]}, got {statement.Fingerprint[..8]}.");
        }

        if (baseline.DurationMs is double expected
            && statement.Summary.QueryElapsedMs is double actual
            && actual > expected * (1 + baseline.DurationToleranceFraction))
        {
            return new RegressionCheckResult(false, $"Duration regressed: baseline {expected:N1} ms, current {actual:N1} ms.");
        }

        return new RegressionCheckResult(true, $"Baseline passed: fingerprint {statement.Fingerprint[..8]} and duration are within bounds.");
    }

    public static string PathFor(string planPath) => planPath + ".baseline.json";
}
