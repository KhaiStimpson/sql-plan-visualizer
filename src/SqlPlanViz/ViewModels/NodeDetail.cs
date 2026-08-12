using SqlPlanViz.Common;
using SqlPlanViz.Model;

namespace SqlPlanViz.ViewModels;

/// <summary>
/// Pre-formatted view of the selected operator for the detail panel (TDD §8). Formatting
/// lives here rather than in a pile of XAML converters.
/// </summary>
public sealed class NodeDetail
{
    private readonly PlanNode _node;
    private readonly PlanStatement _statement;

    public NodeDetail(PlanNode node, PlanStatement statement)
    {
        _node = node;
        _statement = statement;
        Warnings = node.Warnings.Select(w => new WarningItem(node, w)).ToList();
        OutputList = node.OutputList.ToList();
    }

    public PlanNode Node => _node;

    public string Title => _node.PhysicalOp;

    public string Subtitle =>
        string.IsNullOrEmpty(_node.LogicalOp) || _node.LogicalOp == _node.PhysicalOp
            ? $"Node {_node.NodeId}"
            : $"{_node.LogicalOp} · node {_node.NodeId}";

    public string? ObjectName => _node.ObjectName;

    public bool HasObject => !string.IsNullOrEmpty(_node.ObjectName);

    public bool IsParallel => _node.Parallel;

    // ---- Estimate accuracy -------------------------------------------------

    public bool HasRuntimeStats => _node.HasRuntimeStats;

    public string EstimatedRowsText => Format.Rows(_node.EstimatedRowsTotal);

    public string ActualRowsText => _node.ActualRows is double a ? Format.Rows(a) : "–";

    public string EstimatePerExecutionNote =>
        _node.EstimatedExecutions > 1
            ? $"{Format.Rows(_node.EstimatedRows)} per execution × {Format.Rows(_node.EstimatedExecutions)} executions"
            : string.Empty;

    public bool HasEstimatePerExecutionNote => _node.EstimatedExecutions > 1;

    public bool HasBadEstimate => _node.HasBadEstimate;

    public string EstimateVerdict
    {
        get
        {
            if (_node.EstimateErrorFactor is not double f)
            {
                return "Estimated plan — no runtime row counts to compare against.";
            }

            if (f < 2)
            {
                return "Estimate matched reality closely.";
            }

            if (f < 10)
            {
                return $"Estimate was {Format.Factor(f)} out — worth a look, but not alarming.";
            }

            var direction = (_node.ActualRows ?? 0) > _node.EstimatedRowsTotal ? "under" : "over";
            return $"Estimate {direction}shot by {Format.Factor(f)} — the classic bad-estimate signal.";
        }
    }

    /// <summary>Bar fractions (0–100) for the estimated/actual comparison.</summary>
    public double EstimatedBar => Bar(_node.EstimatedRowsTotal);

    public double ActualBar => Bar(_node.ActualRows ?? 0);

    private double Bar(double value)
    {
        var max = Math.Max(_node.EstimatedRowsTotal, _node.ActualRows ?? 0);
        if (max <= 0)
        {
            return 0;
        }

        // Log scale, otherwise a 1000x miss renders the smaller bar as nothing at all.
        return 100 * (Math.Log(1 + value) / Math.Log(1 + max));
    }

    // ---- Cost --------------------------------------------------------------

    public string SubtreeCostText => Format.Cost(_node.EstimatedSubtreeCost);

    public string OperatorCostText => Format.Cost(_node.EstimatedOperatorCost);

    public string CpuCostText => Format.Cost(_node.EstimatedCpuCost);

    public string IoCostText => Format.Cost(_node.EstimatedIoCost);

    public string CostShareText
    {
        get
        {
            var total = _statement.Summary.TotalSubtreeCost;
            return total <= 0
                ? "–"
                : Format.Percent(_node.EstimatedSubtreeCost / total) + " of the statement";
        }
    }

    public double CpuBar => CostSplit(_node.EstimatedCpuCost);

    public double IoBar => CostSplit(_node.EstimatedIoCost);

    private double CostSplit(double part)
    {
        var total = _node.EstimatedCpuCost + _node.EstimatedIoCost;
        return total <= 0 ? 0 : 100 * (part / total);
    }

    // ---- Timing ------------------------------------------------------------

    public bool HasTiming => _node.ActualElapsedMs.HasValue;

    public string ElapsedText => _node.ActualElapsedMs is double ms ? Format.Milliseconds(ms) : "–";

    public string ActualCpuText => _node.ActualCpuMs is double ms ? Format.Milliseconds(ms) : "–";

    public string ExecutionsText => _node.ActualExecutions is int n ? Format.Rows(n) : "–";

    // ---- Detail text -------------------------------------------------------

    public string? Predicate => _node.Predicate;

    public bool HasPredicate => !string.IsNullOrWhiteSpace(_node.Predicate);

    public string? SeekPredicate => _node.SeekPredicate;

    public bool HasSeekPredicate => !string.IsNullOrWhiteSpace(_node.SeekPredicate);

    public IReadOnlyList<string> OutputList { get; }

    public bool HasOutputList => OutputList.Count > 0;

    public string OutputListHeader => $"Output list ({OutputList.Count})";

    public IReadOnlyList<WarningItem> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;
}

/// <summary>A warning plus the operator it came from, so the list can jump to the node.</summary>
public sealed class WarningItem
{
    public WarningItem(PlanNode node, PlanWarning warning)
    {
        Node = node;
        Warning = warning;
    }

    public PlanNode Node { get; }

    public PlanWarning Warning { get; }

    public string Title => Warning.DisplayName;

    public string Location => $"{Node.PhysicalOp} · node {Node.NodeId}";

    public string? Detail => Warning.Detail;

    public bool HasDetail => !string.IsNullOrWhiteSpace(Warning.Detail);

    public WarningSeverity Severity => Warning.Severity;

    public string SeverityGlyph => Warning.Severity switch
    {
        WarningSeverity.Critical => "", // ErrorBadge
        WarningSeverity.Warning => "",  // Warning
        _ => "",                        // Info
    };
}
