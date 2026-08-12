using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics.Rules;

/// <summary>
/// A Nested Loop whose inner subtree cost × executions is large (tuning-roadmap.md Phase
/// 3.5): the inner side runs once per outer row, so a cheap-looking inner subtree becomes
/// expensive once multiplied by the outer row count. Distinct from
/// <see cref="KeyLookupStormRule"/> — this catches any expensive inner subtree, not just a
/// bare Key/RID Lookup.
/// </summary>
public sealed class FatInnerSideLoopRule : IPlanRule
{
    private const double MinEffectiveCost = 5.0;

    public string RuleId => "fat-inner-side-loop";

    public IEnumerable<PlanFinding> Analyse(PlanStatement statement)
    {
        var totalCost = statement.Summary.TotalSubtreeCost;

        foreach (var node in statement.AllNodes)
        {
            if (node.PhysicalOp != "Nested Loops" || node.Children.Count < 2)
            {
                continue;
            }

            var outer = node.Children[0];
            var inner = node.Children[1];

            var executions = outer.ActualRows ?? outer.EstimatedRowsTotal;
            if (executions <= 1)
            {
                continue;
            }

            var effectiveCost = inner.EstimatedSubtreeCost * executions;
            if (effectiveCost < MinEffectiveCost || effectiveCost < totalCost * 0.3)
            {
                continue;
            }

            var impact = totalCost > 0 ? Math.Clamp(node.EstimatedSubtreeCost / totalCost, 0, 1) : 0;
            var target = inner.ObjectName ?? inner.PhysicalOp;

            yield return new PlanFinding
            {
                RuleId = RuleId,
                Title = $"Expensive inner side of Nested Loop — {target} runs {executions:N0} times",
                Severity = FindingSeverity.Critical,
                Confidence = FindingConfidence.Likely,
                Nodes = [node, inner],
                Why = $"The inner side of this Nested Loop ({target}, per-execution cost "
                      + $"{inner.EstimatedSubtreeCost:0.###}) runs once per outer row — about {executions:N0} "
                      + $"times — for an effective cost of roughly {effectiveCost:0.#}, which dominates this "
                      + "plan even though the inner subtree looks cheap in isolation.",
                Fixes =
                [
                    new Fix(
                        $"Add an index that makes the {target} seek cheaper, or covers it entirely.",
                        null,
                        "Index write cost depends on this table's write volume — check that first.",
                        FixKind.Index),
                    new Fix(
                        "As a fallback, hint a hash or merge join instead of nested loops.",
                        "OPTION (HASH JOIN)",
                        "Overrides the optimizer's choice for every execution of this query, not just this one.",
                        FixKind.Hint),
                ],
                ImpactFraction = impact,
            };
        }
    }
}
