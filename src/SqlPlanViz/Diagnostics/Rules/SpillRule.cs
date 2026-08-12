using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics.Rules;

/// <summary>
/// A sort/hash/exchange spill warning (tuning-roadmap.md Phase 2.8), cross-referenced with
/// <see cref="MemoryGrantInfo"/> from Phase 1 when the statement's grant-owning operator
/// carries one. A spill is almost always caused by an underestimate downstream: the operator
/// planned for less memory than it needed and had to write to tempdb.
/// </summary>
public sealed class SpillRule : IPlanRule
{
    private static readonly HashSet<string> SpillWarningTypes =
    [
        "SortSpillDetails",
        "HashSpillDetails",
        "ExchangeSpillDetails",
        "SpillToTempDb",
    ];

    public string RuleId => "spill-to-tempdb";

    public IEnumerable<PlanFinding> Analyse(PlanStatement statement)
    {
        var totalCost = statement.Summary.TotalSubtreeCost;
        var grant = statement.Root.MemoryGrant;

        foreach (var node in statement.AllNodes)
        {
            var warning = node.Warnings.FirstOrDefault(w => SpillWarningTypes.Contains(w.Type));
            if (warning is null)
            {
                continue;
            }

            var impact = totalCost > 0 ? Math.Clamp(node.EstimatedSubtreeCost / totalCost, 0, 1) : 0;

            var grantText = grant is { GrantedMemoryKb: double granted }
                ? $" The statement's memory grant was {granted:N0}KB"
                  + (grant.MaxUsedMemoryKb is double used ? $", used {used:N0}KB." : ".")
                : string.Empty;

            yield return new PlanFinding
            {
                RuleId = RuleId,
                Title = $"{node.PhysicalOp} spilled to tempdb",
                Severity = FindingSeverity.Critical,
                Confidence = FindingConfidence.High,
                Nodes = [node],
                Why = $"{node.PhysicalOp} ran out of its granted memory and spilled to tempdb "
                      + $"({warning.Detail ?? "no further detail in this plan"}).{grantText} "
                      + "This is usually caused by an underestimate upstream — the optimizer sized "
                      + "the memory grant for fewer rows than actually arrived.",
                Fixes =
                [
                    new Fix(
                        "Update statistics on the tables feeding this operator — a spill is usually a downstream underestimate.",
                        null,
                        null,
                        FixKind.Statistics),
                    new Fix(
                        "As a last resort, force a larger memory grant with a query hint.",
                        "OPTION (MIN_GRANT_PERCENT = ...)",
                        "A hint fixes this query but can starve memory for others running concurrently.",
                        FixKind.Hint),
                ],
                ImpactFraction = impact,
            };
        }
    }
}
