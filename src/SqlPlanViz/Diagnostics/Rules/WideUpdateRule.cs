using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics.Rules;

/// <summary>
/// Many index-update operators under one DML statement (tuning-roadmap.md Phase 3.9). Every
/// index on the target table that covers a changed column has to be maintained, so a single
/// logical UPDATE/INSERT/DELETE can fan out into many physical writes.
/// </summary>
public sealed class WideUpdateRule : IPlanRule
{
    private const int ManyIndexUpdates = 3;

    public string RuleId => "wide-update";

    public IEnumerable<PlanFinding> Analyse(PlanStatement statement)
    {
        var updateNodes = statement.AllNodes
            .Where(n => n.LogicalOp is "Update" or "Insert" or "Delete" && n.ObjectName is not null)
            .ToList();

        if (updateNodes.Count < ManyIndexUpdates)
        {
            return [];
        }

        var totalCost = statement.Summary.TotalSubtreeCost;
        var totalWriteCost = updateNodes.Sum(n => n.EstimatedOperatorCost);
        var impact = totalCost > 0 ? Math.Clamp(totalWriteCost / totalCost, 0, 1) : 0;
        var targets = string.Join(", ", updateNodes.Select(n => n.ObjectName).Distinct());

        return
        [
            new PlanFinding
            {
                RuleId = RuleId,
                Title = $"Wide update — {updateNodes.Count} indexes maintained by this DML",
                Severity = updateNodes.Count >= 6 ? FindingSeverity.Warning : FindingSeverity.Info,
                Confidence = FindingConfidence.High,
                Nodes = updateNodes,
                Why = $"This statement maintains {updateNodes.Count} indexes: {targets}. Every one of them "
                      + "has to be updated for each row this statement touches, which multiplies the write "
                      + "cost of what looks like a single logical change.",
                Fixes =
                [
                    new Fix(
                        "Review whether every one of these indexes is still earning its write cost — an unused or redundant index here is pure overhead on this statement.",
                        null,
                        "Dropping an index affects every query that currently benefits from it — check usage stats before removing one.",
                        FixKind.Investigate),
                ],
                ImpactFraction = impact,
            },
        ];
    }
}
