using SqlPlanViz.Diagnostics.Rules;
using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics;

/// <summary>
/// Runs every <see cref="IPlanRule"/> over a statement and ranks the combined findings.
/// A rule that throws is swallowed and skipped — one bad rule must never break plan viewing
/// (tuning-roadmap.md ground rules).
/// </summary>
public sealed class RuleEngine
{
    /// <summary>
    /// Built-in rules, added here as each is implemented (tuning-roadmap.md Phase 2/3).
    /// </summary>
    private static readonly IReadOnlyList<IPlanRule> BuiltInRules =
    [
        new EstimateBlowupOriginRule(),
        new KeyLookupStormRule(),
        new ResidualPredicateScanRule(),
        new ImplicitConversionRule(),
        new SpillRule(),
        new NonSargablePredicateRule(),
        new ParameterSniffingRule(),
        new ParallelismSkewRule(),
        new StaleStatisticsRule(),
        new OptimizerGaveUpRule(),
        new FatInnerSideLoopRule(),
        new SpoolTrapRule(),
        new ScalarUdfRule(),
        new WaitDominatedRule(),
        new WideUpdateRule(),
        new MissingIndexMergeRule(),
        new CostModelDivergenceRule(),
    ];

    private readonly IReadOnlyList<IPlanRule> _rules;

    public RuleEngine(IEnumerable<IPlanRule>? rules = null)
    {
        _rules = rules?.ToList() ?? BuiltInRules;
    }

    /// <summary>Ranked by <see cref="FindingSeverity"/>, then <see cref="PlanFinding.ImpactFraction"/>, then <see cref="FindingConfidence"/>.</summary>
    public IReadOnlyList<PlanFinding> Analyse(PlanStatement statement)
    {
        var findings = new List<PlanFinding>();
        foreach (var rule in _rules)
        {
            try
            {
                findings.AddRange(rule.Analyse(statement));
            }
            catch
            {
                // A misbehaving rule must not take down plan viewing.
            }
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.ImpactFraction)
            .ThenByDescending(f => f.Confidence)
            .ToList();
    }
}
