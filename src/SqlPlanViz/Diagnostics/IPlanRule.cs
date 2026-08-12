using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics;

/// <summary>
/// One diagnostic rule. Pure logic — no WinUI/Win2D types (tuning-roadmap.md ground rules).
/// A rule that throws is caught and skipped by <see cref="RuleEngine"/>; it must never break
/// plan viewing.
/// </summary>
public interface IPlanRule
{
    /// <summary>Stable slug matching <see cref="PlanFinding.RuleId"/>, e.g. "key-lookup-storm".</summary>
    string RuleId { get; }

    IEnumerable<PlanFinding> Analyse(PlanStatement statement);
}
