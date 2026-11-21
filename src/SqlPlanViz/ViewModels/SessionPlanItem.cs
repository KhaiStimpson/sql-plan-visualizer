using CommunityToolkit.Mvvm.ComponentModel;
using SqlPlanViz.Model;

namespace SqlPlanViz.ViewModels;

public sealed partial class SessionPlanItem : ObservableObject
{
    public required ExecutionPlan Plan { get; init; }

    public string? SourcePath { get; init; }

    public DateTime CapturedAt { get; init; } = DateTime.Now;

    /// <summary>The plan on screen; the rail highlights it.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    public string Title => Plan.SourceName ?? "Captured plan";

    public string Subtitle =>
        $"{Plan.Statements.Count} statement{(Plan.Statements.Count == 1 ? string.Empty : "s")} · {RailSubtitle}";

    /// <summary>The rail's second line: "9:33 PM · a0adb2e1".</summary>
    public string RailSubtitle
    {
        get
        {
            var statement = Plan.Statements.OrderByDescending(s => s.Summary.TotalSubtreeCost).First();
            return $"{CapturedAt:t} · {statement.Fingerprint[..8]}";
        }
    }
}
