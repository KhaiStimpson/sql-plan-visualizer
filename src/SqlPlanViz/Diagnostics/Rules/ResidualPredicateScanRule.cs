using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics.Rules;

/// <summary>
/// A Scan or Seek carrying a residual <see cref="PlanNode.Predicate"/> whose row count
/// vastly exceeds the rows its parent actually keeps (tuning-roadmap.md Phase 2.6). The
/// predicate is filtering row-by-row after the read instead of an index doing the
/// elimination, so the operator reads far more than the query ever uses.
/// </summary>
public sealed class ResidualPredicateScanRule : IPlanRule
{
    private const double MinSelectivityRatio = 10;

    public string RuleId => "residual-predicate-scan";

    public IEnumerable<PlanFinding> Analyse(PlanStatement statement)
    {
        var totalCost = statement.Summary.TotalSubtreeCost;

        foreach (var (parent, node) in WalkWithParent(statement.Root))
        {
            if (parent is null)
            {
                continue;
            }

            if (!node.PhysicalOp.Contains("Scan", StringComparison.OrdinalIgnoreCase)
                && !node.PhysicalOp.Contains("Seek", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrEmpty(node.Predicate))
            {
                continue;
            }

            if (node.ActualRows is not double rows || parent.ActualRows is not double parentRows || parentRows <= 0)
            {
                continue;
            }

            var ratio = rows / Math.Max(parentRows, 1);
            if (ratio < MinSelectivityRatio)
            {
                continue;
            }

            var impact = totalCost > 0 ? Math.Clamp(node.EstimatedSubtreeCost / totalCost, 0, 1) : 0;
            var target = node.ObjectName ?? node.PhysicalOp;

            yield return new PlanFinding
            {
                RuleId = RuleId,
                Title = $"Residual predicate on {target} discards {ratio:0.#}x the rows it reads",
                Severity = ratio >= 50 ? FindingSeverity.Critical : FindingSeverity.Warning,
                Confidence = FindingConfidence.Likely,
                Nodes = [node],
                Why = $"{node.PhysicalOp} on {target} returned {rows:N0} row(s), but only "
                      + $"{parentRows:N0} were kept above it — a selectivity of about 1 in {ratio:0.#}. "
                      + "The filter is applied row-by-row after the read instead of by an index, "
                      + $"via the predicate: {node.Predicate}",
                Fixes =
                [
                    new Fix(
                        $"Add an index on {target} covering the predicate's column(s) so it can seek instead of scan-and-filter.",
                        null,
                        "Index write cost depends on the table's insert/update volume — check that first.",
                        FixKind.Index),
                ],
                ImpactFraction = impact,
            };
        }
    }

    private static IEnumerable<(PlanNode? Parent, PlanNode Node)> WalkWithParent(PlanNode root)
    {
        yield return (null, root);
        foreach (var pair in WalkChildren(root))
        {
            yield return pair;
        }

        static IEnumerable<(PlanNode? Parent, PlanNode Node)> WalkChildren(PlanNode node)
        {
            foreach (var child in node.Children)
            {
                yield return (node, child);
                foreach (var pair in WalkChildren(child))
                {
                    yield return pair;
                }
            }
        }
    }
}
