using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics.Rules;

/// <summary>
/// <see cref="CompileInfo.EarlyAbortReason"/> is "TimeOut" or "MemoryLimitExceeded"
/// (tuning-roadmap.md Phase 3.4): the optimizer stopped searching before it finished
/// considering plans. Pure context — no fix is generated — but context nobody else surfaces.
/// </summary>
public sealed class OptimizerGaveUpRule : IPlanRule
{
    public string RuleId => "optimizer-gave-up";

    public IEnumerable<PlanFinding> Analyse(PlanStatement statement)
    {
        var reason = statement.Summary.Compile?.EarlyAbortReason;
        if (reason is not ("TimeOut" or "MemoryLimitExceeded"))
        {
            return [];
        }

        var explanation = reason == "TimeOut"
            ? "the optimizer ran out of time and settled for the best plan it had found so far"
            : "the optimizer ran out of memory while searching for a plan and settled for the best one it had found so far";

        return
        [
            new PlanFinding
            {
                RuleId = RuleId,
                Title = $"Optimizer gave up early ({reason})",
                Severity = FindingSeverity.Info,
                Confidence = FindingConfidence.High,
                Nodes = [statement.Root],
                Why = $"This plan was never fully considered — {explanation}. The plan you are looking at "
                      + "may not be the best one available for this query, which is worth knowing before "
                      + "spending time tuning the shape it happened to land on.",
                Fixes =
                [
                    new Fix(
                        "Simplify the query (fewer joins/subqueries per statement) so the optimizer can search the full space, or investigate whether a simpler, equivalent rewrite exists.",
                        null,
                        null,
                        FixKind.Investigate),
                ],
                ImpactFraction = 0,
            },
        ];
    }
}
