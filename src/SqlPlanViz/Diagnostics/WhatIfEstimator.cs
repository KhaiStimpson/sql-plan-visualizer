using SqlPlanViz.Common;
using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics;

public sealed record WhatIfEstimate(double CurrentImpactFraction, double ProjectedImpactFraction, double EstimatedCostReduction)
{
    public string Summary => $"What if: this work could fall from {Format.Percent(CurrentImpactFraction)} to about {Format.Percent(ProjectedImpactFraction)} of the plan, "
                             + $"saving roughly {Format.Cost(EstimatedCostReduction)} estimated cost units.";
}

/// <summary>
/// Coarse scenario ranking, deliberately not a replacement for re-capturing a plan. Rule-specific
/// reduction factors estimate direction and relative payoff rather than optimizer-grade costs.
/// </summary>
public static class WhatIfEstimator
{
    public static WhatIfEstimate? Estimate(PlanStatement statement, PlanFinding finding)
    {
        if (finding.ImpactFraction <= 0 || finding.Fixes.Count == 0)
        {
            return null;
        }

        var retainedFraction = finding.RuleId switch
        {
            "key-lookup-storm" => 0.06,
            "residual-predicate-scan" => 0.15,
            "non-sargable-predicate" => 0.20,
            "fat-inner-side-loop" => 0.25,
            "implicit-conversion" => 0.35,
            "spill-to-tempdb" => 0.55,
            "stale-statistics" => 0.60,
            _ => 0.65,
        };

        var projected = finding.ImpactFraction * retainedFraction;
        var reduction = statement.Summary.TotalSubtreeCost * (finding.ImpactFraction - projected);
        return new WhatIfEstimate(finding.ImpactFraction, projected, Math.Max(0, reduction));
    }
}
