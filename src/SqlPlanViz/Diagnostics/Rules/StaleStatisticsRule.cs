using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics.Rules;

/// <summary>
/// Statistics with a high modification count, or a column with none at all, combined with a
/// bad estimate somewhere in the plan (tuning-roadmap.md Phase 3.3). A modification count on
/// its own is not proof — every table changes — but paired with an actual bad estimate it
/// becomes a concrete, actionable finding.
/// </summary>
public sealed class StaleStatisticsRule : IPlanRule
{
    private const long HighModificationCount = 10_000;

    public string RuleId => "stale-statistics";

    public IEnumerable<PlanFinding> Analyse(PlanStatement statement)
    {
        if (!statement.AllNodes.Any(n => n.HasBadEstimate))
        {
            return [];
        }

        var totalCost = statement.Summary.TotalSubtreeCost;
        var findings = new List<PlanFinding>();

        foreach (var stats in statement.Summary.StatisticsUsed)
        {
            if (stats.ModificationCount is not long mods || mods < HighModificationCount)
            {
                continue;
            }

            var target = $"{stats.Schema}.{stats.Table}".Trim('.');
            var worst = statement.AllNodes
                .Where(n => n.HasBadEstimate && string.Equals(n.ObjectName, target, StringComparison.OrdinalIgnoreCase)
                            || (n.ObjectName?.StartsWith(target, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderByDescending(n => n.EstimateErrorFactor)
                .FirstOrDefault()
                ?? statement.AllNodes.Where(n => n.HasBadEstimate).OrderByDescending(n => n.EstimateErrorFactor).First();

            var impact = totalCost > 0 ? Math.Clamp(worst.EstimatedSubtreeCost / totalCost, 0, 1) : 0;

            findings.Add(new PlanFinding
            {
                RuleId = RuleId,
                Title = $"Stale statistics on {target}.{stats.StatisticsName} — {mods:N0} modifications since last update",
                Severity = FindingSeverity.Warning,
                Confidence = FindingConfidence.Likely,
                Nodes = [worst],
                Why = $"{target}.{stats.StatisticsName} has had {mods:N0} row modifications since it was "
                      + (stats.LastUpdate is DateTime d ? $"last updated on {d:yyyy-MM-dd}" : "last updated")
                      + $", and this plan has a {worst.EstimateErrorFactor:0.#}x row estimate error at "
                      + $"{worst.ObjectName ?? worst.PhysicalOp} — consistent with the optimizer working from "
                      + "an outdated row/value distribution.",
                Fixes =
                [
                    new Fix(
                        $"Update statistics on {target}.{stats.StatisticsName}.",
                        $"UPDATE STATISTICS {target} ({stats.StatisticsName});",
                        "A full scan update can be expensive on a large table — consider a sampled update or an off-hours window.",
                        FixKind.Statistics),
                ],
                ImpactFraction = impact,
            });
        }

        foreach (var node in statement.AllNodes)
        {
            var warning = node.Warnings.FirstOrDefault(w => w.Type == "ColumnsWithNoStatistics");
            if (warning is null || !node.HasBadEstimate)
            {
                continue;
            }

            var impact = totalCost > 0 ? Math.Clamp(node.EstimatedSubtreeCost / totalCost, 0, 1) : 0;
            var target = node.ObjectName ?? node.PhysicalOp;

            findings.Add(new PlanFinding
            {
                RuleId = RuleId,
                Title = $"No statistics on {target}'s join/filter column(s)",
                Severity = FindingSeverity.Warning,
                Confidence = FindingConfidence.Likely,
                Nodes = [node],
                Why = $"{target} has no statistics on: {warning.Detail}. This plan has a "
                      + $"{node.EstimateErrorFactor:0.#}x row estimate error here — the optimizer had "
                      + "nothing to base its cardinality estimate on.",
                Fixes =
                [
                    new Fix(
                        "Create statistics on the affected column(s), or enable AUTO_CREATE_STATISTICS if it's off.",
                        $"CREATE STATISTICS ... ON {target} (...);",
                        null,
                        FixKind.Statistics),
                ],
                ImpactFraction = impact,
            });
        }

        return findings;
    }
}
