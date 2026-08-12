using SqlPlanViz.Common;
using SqlPlanViz.Diagnostics;
using SqlPlanViz.Model;

namespace SqlPlanViz.ViewModels;

public sealed class PlanDeltaItem
{
    public required PlanNodeDelta Delta { get; init; }

    public PlanNode? TargetNode => Delta.After;

    public string Operator => Delta.DisplayName;

    public string ObjectName => Delta.After?.ObjectName ?? Delta.Before?.ObjectName ?? "—";

    public string Change => Delta.Kind.ToString();

    public string CostDeltaText => Signed(Delta.CostDelta, Format.Cost);

    public string RowsDeltaText => Delta.RowsDelta is double rows ? Signed(rows, Format.Rows) : "—";

    private static string Signed(double value, Func<double, string> format) =>
        value > 0 ? "+" + format(value) : value < 0 ? "−" + format(Math.Abs(value)) : "—";
}
