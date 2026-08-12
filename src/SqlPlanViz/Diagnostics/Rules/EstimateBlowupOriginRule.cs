using System.Globalization;
using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics.Rules;

/// <summary>
/// The highest-value rule (tuning-roadmap.md Phase 2.4): walks the tree and finds the
/// deepest node where the estimate first crosses 10x wrong — i.e. the error is not simply
/// inherited from a child. Everything above that node is collateral damage from the same
/// root cause, so only the origin is reported.
/// </summary>
public sealed class EstimateBlowupOriginRule : IPlanRule
{
    public string RuleId => "estimate-blowup-origin";

    public IEnumerable<PlanFinding> Analyse(PlanStatement statement)
    {
        var totalCost = statement.Summary.TotalSubtreeCost;

        foreach (var node in statement.AllNodes)
        {
            if (!node.HasBadEstimate)
            {
                continue;
            }

            // Inherited from a child that is already blown up — not the origin.
            if (node.Children.Any(c => c.HasBadEstimate))
            {
                continue;
            }

            var factor = node.EstimateErrorFactor!.Value;
            var actual = node.ActualRows!.Value;
            var estimated = node.EstimatedRowsTotal;
            var direction = actual > estimated ? "under" : "over";
            var target = node.ObjectName ?? node.PhysicalOp;

            var impact = totalCost > 0 ? Math.Clamp(node.EstimatedSubtreeCost / totalCost, 0, 1) : 0;

            yield return new PlanFinding
            {
                RuleId = RuleId,
                Title = $"Row estimate first goes wrong here — {Format(factor)}x {direction} at the {target}",
                Severity = factor >= 20 ? FindingSeverity.Critical : FindingSeverity.Warning,
                Confidence = FindingConfidence.High,
                Nodes = [node],
                Why = $"{node.PhysicalOp} estimated {Format(estimated)} row(s) but actually returned "
                      + $"{Format(actual)} — {Format(factor)}x {direction}. This is the deepest point where "
                      + "the estimate first goes wrong; every operator above it is working from a bad number "
                      + "inherited from here.",
                Fixes =
                [
                    new Fix(
                        "Check statistics freshness and look for a non-sargable predicate or parameter sniffing at this operator.",
                        null,
                        null,
                        FixKind.Investigate),
                ],
                ImpactFraction = impact,
            };
        }
    }

    private static string Format(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
