using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics.Rules;

/// <summary>
/// A sniffed parameter (compiled value differs from the runtime value) combined with a bad
/// estimate somewhere in the plan (tuning-roadmap.md Phase 3.1). Either fact alone is
/// ambiguous; together they turn "maybe it's sniffing" into evidence.
/// </summary>
public sealed class ParameterSniffingRule : IPlanRule
{
    public string RuleId => "parameter-sniffing";

    public IEnumerable<PlanFinding> Analyse(PlanStatement statement)
    {
        var sniffed = statement.Summary.Parameters.Where(p => p.Sniffed).ToList();
        if (sniffed.Count == 0)
        {
            return [];
        }

        var badEstimateNode = statement.AllNodes
            .Where(n => n.HasBadEstimate)
            .OrderByDescending(n => n.EstimateErrorFactor)
            .FirstOrDefault();

        if (badEstimateNode is null)
        {
            return [];
        }

        var totalCost = statement.Summary.TotalSubtreeCost;
        var impact = totalCost > 0 ? Math.Clamp(badEstimateNode.EstimatedSubtreeCost / totalCost, 0, 1) : 0;
        var names = string.Join(", ", sniffed.Select(p => p.Name));

        var finding = new PlanFinding
        {
            RuleId = RuleId,
            Title = $"Parameter sniffing likely — {names} compiled for a different value than it ran with",
            Severity = FindingSeverity.Warning,
            Confidence = FindingConfidence.Likely,
            Nodes = [badEstimateNode],
            Why = string.Join(" ", sniffed.Select(p =>
                    $"{p.Name} was compiled for {p.CompiledValue ?? "(unknown)"} but ran with {p.RuntimeValue ?? "(unknown)"}."))
                  + $" This plan has a {badEstimateNode.EstimateErrorFactor:0.#}x row estimate error at "
                  + $"{badEstimateNode.ObjectName ?? badEstimateNode.PhysicalOp}, consistent with a plan "
                  + "compiled for a value with a very different row count than the one it actually ran with.",
            Fixes =
            [
                new Fix(
                    "Add OPTIMIZE FOR a representative value, or OPTIMIZE FOR UNKNOWN to use the average distribution.",
                    "OPTION (OPTIMIZE FOR (@param = <value>))",
                    "Picking a bad representative value can make the plan worse for other callers.",
                    FixKind.Hint),
                new Fix(
                    "Force a fresh plan every execution with RECOMPILE.",
                    "OPTION (RECOMPILE)",
                    "Adds compile-time CPU on every call — fine for infrequent, expensive queries; costly for hot paths.",
                    FixKind.Hint),
                new Fix(
                    "Rewrite using a local variable to make the optimizer use the average distribution instead of sniffing.",
                    null,
                    "Deliberately gives up parameter-specific optimization in exchange for a stable, average-case plan.",
                    FixKind.Rewrite),
            ],
            ImpactFraction = impact,
        };

        return [finding];
    }
}
