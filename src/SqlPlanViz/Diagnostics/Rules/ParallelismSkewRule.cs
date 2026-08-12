using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics.Rules;

/// <summary>
/// <see cref="PlanNode.HasThreadSkew"/> — one thread doing far more work than the others
/// under parallelism (tuning-roadmap.md Phase 3.2). Often the true cause of "sometimes it's
/// slow" and invisible in every aggregate view, since the aggregated row count looks normal.
/// </summary>
public sealed class ParallelismSkewRule : IPlanRule
{
    public string RuleId => "parallelism-skew";

    public IEnumerable<PlanFinding> Analyse(PlanStatement statement)
    {
        var totalCost = statement.Summary.TotalSubtreeCost;

        foreach (var node in statement.AllNodes)
        {
            if (!node.HasThreadSkew)
            {
                continue;
            }

            var mean = node.PerThread.Average(t => t.ActualRows);
            var busiest = node.PerThread.OrderByDescending(t => t.ActualRows).First();
            var impact = totalCost > 0 ? Math.Clamp(node.EstimatedSubtreeCost / totalCost, 0, 1) : 0;
            var target = node.ObjectName ?? node.PhysicalOp;

            yield return new PlanFinding
            {
                RuleId = RuleId,
                Title = $"Parallel thread skew on {target} — thread {busiest.Thread} did {busiest.ActualRows / mean:0.#}x the average",
                Severity = FindingSeverity.Warning,
                Confidence = FindingConfidence.High,
                Nodes = [node],
                Why = $"{node.PhysicalOp} ran across {node.PerThread.Count} threads with an average of "
                      + $"{mean:N0} rows each, but thread {busiest.Thread} processed {busiest.ActualRows:N0} — "
                      + "the query's wall-clock time is set by that one thread, not the average, so this is "
                      + "invisible in any aggregated metric.",
                Fixes =
                [
                    new Fix(
                        "Check for a skewed distribution in the partitioning/join column — one value with disproportionately many rows forces one thread to do most of the work.",
                        null,
                        null,
                        FixKind.Investigate),
                ],
                ImpactFraction = impact,
            };
        }
    }
}
