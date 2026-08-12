using SqlPlanViz.Model;

namespace SqlPlanViz.ViewModels;

public sealed class SessionPlanItem
{
    public required ExecutionPlan Plan { get; init; }

    public string? SourcePath { get; init; }

    public DateTime CapturedAt { get; init; } = DateTime.Now;

    public string Title => Plan.SourceName ?? "Captured plan";

    public string Subtitle
    {
        get
        {
            var statement = Plan.Statements.OrderByDescending(s => s.Summary.TotalSubtreeCost).First();
            return $"{Plan.Statements.Count} statement{(Plan.Statements.Count == 1 ? string.Empty : "s")} · {CapturedAt:t} · {statement.Fingerprint[..8]}";
        }
    }
}
