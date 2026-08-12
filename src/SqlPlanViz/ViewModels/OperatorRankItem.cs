using SqlPlanViz.Common;
using SqlPlanViz.Model;

namespace SqlPlanViz.ViewModels;

public sealed class OperatorRankItem
{
    public required int Rank { get; init; }

    public required PlanNode Node { get; init; }

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
}
