using SqlPlanViz.Common;
using SqlPlanViz.Diagnostics.Rules;
using SqlPlanViz.Model;

namespace SqlPlanViz.ViewModels;

public sealed class OperatorRankItem
{
    public required int Rank { get; init; }

    public required PlanNode Node { get; init; }

    public required PlanStatement Statement { get; init; }

    public string RankText => $"#{Rank}";

    public string Title => Node.PhysicalOp;

    public string Subtitle => string.IsNullOrWhiteSpace(Node.ObjectName)
        ? $"Node {Node.NodeId}"
        : $"Node {Node.NodeId} · {Node.ObjectName}";

    public string PrimaryMetric => Node.SelfTimeMs is double self
        ? $"{Format.Milliseconds(self)} self time"
        : $"{Format.Cost(Node.EstimatedOperatorCost)} operator cost";

    public string RowsText => Node.ActualRows is double actual
        ? $"{Format.Rows(actual)} actual rows"
        : $"{Format.Rows(Node.EstimatedRowsTotal)} estimated rows";

    /// <summary>
    /// Cost-model divergence for this operator (hot-path-plan.md Phase 2) — the same
    /// est-share/actual-share/delta triple <see cref="CostModelDivergenceRule"/> flags on.
    /// Null when there's nothing to compare (no runtime stats, no cost total).
    /// </summary>
    private (double EstimatedShare, double ActualShare, double Delta)? Shares =>
        CostModelDivergenceRule.ComputeShares(Node, Statement);

    public double AbsDivergence => Shares?.Delta ?? -1;

    public bool HasDivergence => Shares is not null;

    public string DivergenceText => Shares is { } s
        ? $"est {Format.Percent(s.EstimatedShare)} · actual {Format.Percent(s.ActualShare)} · Δ {Format.Percent(s.Delta)}"
        : "—";

    /// <summary>Flags the row when the gap exceeds the same threshold the rule fires on.</summary>
    public bool DivergenceExceedsThreshold => Shares?.Delta >= CostModelDivergenceRule.Threshold;
}
