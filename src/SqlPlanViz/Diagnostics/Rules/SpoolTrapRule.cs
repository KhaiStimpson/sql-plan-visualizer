using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics.Rules;

/// <summary>
/// A Table/Index Spool with high rebinds (tuning-roadmap.md Phase 3.6). Usually Halloween
/// protection on a DML statement, or an ORM-generated correlated subquery re-materializing
/// the same data on every outer row.
/// </summary>
public sealed class SpoolTrapRule : IPlanRule
{
    private const int HighRebindThreshold = 50;

    public string RuleId => "spool-trap";

    public IEnumerable<PlanFinding> Analyse(PlanStatement statement)
    {
        var totalCost = statement.Summary.TotalSubtreeCost;

        foreach (var node in statement.AllNodes)
        {
            if (!node.PhysicalOp.Contains("Spool", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Rebinds ≈ executions - 1 for a node whose EstimatedExecutions already folds in
            // rebinds+rewinds+1; approximate rebind count from actual executions.
            var rebinds = (node.ActualExecutions ?? 0) - 1;
            if (rebinds < HighRebindThreshold)
            {
                continue;
            }

            var impact = totalCost > 0 ? Math.Clamp(node.EstimatedSubtreeCost / totalCost, 0, 1) : 0;

            yield return new PlanFinding
            {
                RuleId = RuleId,
                Title = $"{node.PhysicalOp} rebuilt {rebinds:N0} times",
                Severity = FindingSeverity.Warning,
                Confidence = FindingConfidence.Likely,
                Nodes = [node],
                Why = $"{node.PhysicalOp} was rebound and re-materialized about {rebinds:N0} times. "
                      + "This is usually either Halloween protection on an UPDATE/DELETE/MERGE that touches "
                      + "the same index it reads, or a correlated subquery (often ORM-generated) "
                      + "re-running for every outer row instead of being computed once.",
                Fixes =
                [
                    new Fix(
                        "If this is a DML statement, this spool may be required Halloween protection — check whether the plan actually needs it before working around it.",
                        null,
                        null,
                        FixKind.Investigate),
                    new Fix(
                        "If this is a correlated subquery, rewrite it as a join or a pre-aggregated derived table so it runs once instead of per outer row.",
                        null,
                        null,
                        FixKind.Rewrite),
                ],
                ImpactFraction = impact,
            };
        }
    }
}
