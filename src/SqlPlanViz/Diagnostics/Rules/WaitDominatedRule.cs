using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics.Rules;

/// <summary>
/// Total wait time exceeds a large share of elapsed time (tuning-roadmap.md Phase 3.8).
/// Prevents tuning the wrong thing entirely — if most of the elapsed time was spent waiting
/// (locks, latches, network), no amount of plan-shape tuning will help.
/// </summary>
public sealed class WaitDominatedRule : IPlanRule
{
    private const double MinWaitFraction = 0.3;

    public string RuleId => "wait-dominated";

    public IEnumerable<PlanFinding> Analyse(PlanStatement statement)
    {
        var waits = statement.Summary.Waits;
        if (waits.Count == 0 || statement.Summary.QueryElapsedMs is not double elapsedMs || elapsedMs <= 0)
        {
            return [];
        }

        var totalWaitMs = waits.Sum(w => w.WaitTimeMs);
        var fraction = totalWaitMs / elapsedMs;
        if (fraction < MinWaitFraction)
        {
            return [];
        }

        var worst = waits.OrderByDescending(w => w.WaitTimeMs).First();

        return
        [
            new PlanFinding
            {
                RuleId = RuleId,
                Title = "This is not a plan problem — most of the time was spent waiting",
                Severity = FindingSeverity.Critical,
                Confidence = FindingConfidence.High,
                Nodes = [statement.Root],
                Why = $"{totalWaitMs / 1000:0.#}s of {elapsedMs / 1000:0.#}s elapsed time was spent waiting, "
                      + $"dominated by {worst.WaitType} ({worst.WaitTimeMs / 1000:0.#}s across {worst.WaitCount:N0} "
                      + "wait(s)). Reshaping this plan cannot fix time the engine spent blocked rather than "
                      + "executing — the bottleneck is elsewhere.",
                Fixes =
                [
                    new Fix(
                        $"Investigate the {worst.WaitType} wait directly — it points to contention, blocking, or an external resource bottleneck rather than a plan defect.",
                        null,
                        null,
                        FixKind.Investigate),
                ],
                ImpactFraction = 0,
            },
        ];
    }
}
