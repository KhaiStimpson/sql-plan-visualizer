using System.Globalization;
using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics.Rules;

/// <summary>
/// Flags operators where the optimizer's cost model and the clock disagree about how expensive
/// an operator is (hot-path-plan.md Phase 2): its share of the plan's *estimated* cost is far
/// from its share of the plan's *actual* elapsed time. Both sides are exclusive-of-children
/// (operator cost, self time), so this is an apples-to-apples "where is the model wrong"
/// signal, not just "what is slow" (that's <see cref="EstimateBlowupOriginRule"/>'s job).
/// </summary>
public sealed class CostModelDivergenceRule : IPlanRule
{
    /// <summary>A gap below this many percentage points is normal model noise, not a finding.</summary>
    public const double Threshold = 0.15;

    public string RuleId => "cost-model-divergence";

    /// <summary>
    /// The est-cost-share / actual-time-share / |delta| triple this rule flags on, exposed so
    /// the ranked-operator list (MainViewModel/OperatorRankItem) can show the same numbers as a
    /// column without duplicating the math. Null when the statement or node lacks the runtime
    /// stats or cost totals needed to compute it.
    /// </summary>
    public static (double EstimatedShare, double ActualShare, double Delta)? ComputeShares(
        PlanNode node, PlanStatement statement)
    {
        if (node.SelfTimeMs is not double self)
        {
            return null;
        }

        var totalCost = statement.Summary.TotalSubtreeCost;
        var totalElapsed = statement.Summary.QueryElapsedMs ?? statement.Root.ActualElapsedMs;
        if (totalCost <= 0 || totalElapsed is not double elapsed || elapsed <= 0)
        {
            return null;
        }

        var estimatedShare = Math.Clamp(node.EstimatedOperatorCost / totalCost, 0, 1);
        var actualShare = Math.Clamp(self / elapsed, 0, 1);
        return (estimatedShare, actualShare, Math.Abs(estimatedShare - actualShare));
    }

    public IEnumerable<PlanFinding> Analyse(PlanStatement statement)
    {
        if (!statement.HasRuntimeStats)
        {
            return [];
        }

        var findings = new List<PlanFinding>();

        foreach (var node in statement.AllNodes)
        {
            if (ComputeShares(node, statement) is not { } shares)
            {
                continue;
            }

            var (estimatedShare, actualShare, delta) = shares;
            if (delta < Threshold)
            {
                continue;
            }

            var target = node.ObjectName ?? node.PhysicalOp;
            var direction = actualShare > estimatedShare
                ? "costs far more time than the optimizer expected"
                : "costs far less time than the optimizer expected";

            findings.Add(new PlanFinding
            {
                RuleId = RuleId,
                Title = $"Cost model disagrees with the clock at {target} — "
                        + $"est {Percent(estimatedShare)} vs actual {Percent(actualShare)}",
                Severity = delta >= 0.4 ? FindingSeverity.Critical : FindingSeverity.Warning,
                Confidence = FindingConfidence.High,
                Nodes = [node],
                Why = $"{node.PhysicalOp} {direction}: the optimizer priced it at {Percent(estimatedShare)} "
                      + $"of the plan's total estimated cost, but it actually accounted for "
                      + $"{Percent(actualShare)} of the query's elapsed time — a {Percent(delta)} gap.",
                Fixes =
                [
                    new Fix(
                        "A large cost/clock gap usually traces back to a bad cardinality estimate, stale "
                        + "statistics, or a cost-model blind spot (e.g. TVFs, CLR, or wide parallel skew) — "
                        + "check EstimateErrorFactor and warnings on this operator first.",
                        null,
                        null,
                        FixKind.Investigate),
                ],
                ImpactFraction = delta,
            });
        }

        // "Ranked by the gap" is this rule's own contract, independent of RuleEngine's later
        // severity/impact/confidence resort across all rules combined.
        return findings.OrderByDescending(f => f.ImpactFraction).ToList();
    }

    private static string Percent(double fraction) =>
        (fraction * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
