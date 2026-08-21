using SqlPlanViz.Common;
using SqlPlanViz.Editing;

namespace SqlPlanViz.Diagnostics;

/// <summary>
/// What changed, attributed to one line of the editor's text.
///
/// <see cref="Confidence"/> is the weakest link in the chain that produced it: Showplan has no
/// source offsets, so this is inference on top of inference and the number has to travel with
/// the claim.
/// </summary>
public sealed record LineImpact
{
    public int Line { get; init; }

    /// <summary>Signed change in estimated operator cost attributed to this line.</summary>
    public double CostDelta { get; init; }

    public double Confidence { get; init; }

    public GutterMarkKind Kind { get; init; }

    /// <summary>Short text for the gutter tooltip and the end-of-line annotation.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The operator this line is blamed for, so clicking the mark can select it.</summary>
    public int? NodeId { get; init; }
}

/// <summary>
/// Folds a plan diff into per-line impacts (live-plan-editor-plan.md Phase 5).
///
/// Every changed operator is mapped back to a span with <see cref="SqlNodeMapper"/>, the spans
/// are collapsed to lines, and the lines are scored. The plan is explicit that a confidently
/// wrong gutter arrow is worse than no arrow, so nothing below
/// <see cref="SqlNodeMapper.MinimumUsefulConfidence"/> is returned at all, and the wording
/// that does come back says "likely".
/// </summary>
public static class SqlDeltaMapper
{
    /// <summary>Cost changes below this fraction of the total are not worth a mark.</summary>
    private const double MaterialFraction = 0.02;

    public static IReadOnlyList<LineImpact> Map(
        string sql,
        PlanDiffResult? diff,
        SqlParserVersion? parserVersion = null)
    {
        if (diff is null || string.IsNullOrWhiteSpace(sql))
        {
            return [];
        }

        // Built once and shared: mapping forty operators would otherwise re-parse forty times.
        var index = SqlAstIndex.Build(sql, parserVersion);
        var lineStarts = LineStarts(sql);
        var total = Math.Max(diff.After.Summary.TotalSubtreeCost, 0.0001);

        var byLine = new Dictionary<int, Accumulator>();

        foreach (var delta in diff.Nodes)
        {
            if (delta.Kind == PlanDiffKind.Unchanged)
            {
                continue;
            }

            var node = delta.After ?? delta.Before;
            if (node is null)
            {
                continue;
            }

            var span = SqlNodeMapper.Map(sql, node, index);
            if (span is null || span.Confidence < SqlNodeMapper.MinimumUsefulConfidence)
            {
                continue;
            }

            var cost = delta.Kind switch
            {
                PlanDiffKind.Added => delta.After!.EstimatedOperatorCost,
                PlanDiffKind.Removed => -delta.Before!.EstimatedOperatorCost,
                _ => delta.CostDelta,
            };

            var line = LineOf(lineStarts, span.Start);
            if (!byLine.TryGetValue(line, out var accumulator))
            {
                accumulator = new Accumulator();
                byLine[line] = accumulator;
            }

            accumulator.Add(delta, node.NodeId, cost, span.Confidence);
        }

        var impacts = new List<LineImpact>();
        foreach (var (line, accumulator) in byLine)
        {
            // An operator that moved by a rounding error is not a result. Something that was
            // added or removed always is, however cheap — a Key Lookup appearing matters even
            // when the optimizer thinks it is free.
            if (!accumulator.HasStructuralChange && Math.Abs(accumulator.CostDelta) / total < MaterialFraction)
            {
                continue;
            }

            impacts.Add(new LineImpact
            {
                Line = line,
                CostDelta = accumulator.CostDelta,
                Confidence = accumulator.Confidence,
                Kind = accumulator.Kind,
                Text = accumulator.Describe(),
                NodeId = accumulator.NodeId,
            });
        }

        return [.. impacts.OrderBy(i => i.Line)];
    }

    /// <summary>Turns impacts into gutter marks. Everything here already passed the threshold.</summary>
    public static IReadOnlyList<GutterMark> ToGutterMarks(IEnumerable<LineImpact> impacts) =>
    [
        .. impacts.Select(i => new GutterMark
        {
            Line = i.Line,
            Kind = i.Kind,
            Tooltip = $"{i.Text} (likely — {i.Confidence * 100:0}% confidence)",
            NodeId = i.NodeId,
        }),
    ];

    public static IReadOnlyList<InlineAnnotation> ToAnnotations(IEnumerable<LineImpact> impacts) =>
    [
        .. impacts.Select(i => new InlineAnnotation
        {
            Line = i.Line,
            Kind = i.Kind,
            Text = "likely " + i.Text,
        }),
    ];

    private sealed class Accumulator
    {
        private readonly List<string> _descriptions = [];

        public double CostDelta { get; private set; }

        public double Confidence { get; private set; } = 1;

        public bool HasStructuralChange { get; private set; }

        public int? NodeId { get; private set; }

        public GutterMarkKind Kind => HasAdded
            ? GutterMarkKind.Added
            : CostDelta > 0
                ? GutterMarkKind.Regressed
                : GutterMarkKind.Improved;

        private bool HasAdded { get; set; }

        public void Add(PlanNodeDelta delta, int nodeId, double cost, double confidence)
        {
            CostDelta += cost;

            // The line is only as trustworthy as its least trustworthy attribution.
            Confidence = Math.Min(Confidence, confidence);

            // Blame the operator that moved the most, since that is the one worth selecting.
            if (NodeId is null || Math.Abs(cost) > Math.Abs(_largestCost))
            {
                NodeId = nodeId;
                _largestCost = cost;
            }

            switch (delta.Kind)
            {
                case PlanDiffKind.Added:
                    HasAdded = true;
                    HasStructuralChange = true;
                    _descriptions.Add($"{delta.After!.PhysicalOp} added");
                    break;

                case PlanDiffKind.Removed:
                    HasStructuralChange = true;
                    _descriptions.Add($"{delta.Before!.PhysicalOp} removed");
                    break;

                default:
                    _descriptions.Add(
                        $"{delta.After?.PhysicalOp ?? delta.Before?.PhysicalOp} {(cost > 0 ? "costlier" : "cheaper")}");
                    break;
            }
        }

        private double _largestCost;

        public string Describe()
        {
            var summary = _descriptions.Count switch
            {
                0 => "changed",
                1 => _descriptions[0],
                _ => $"{_descriptions[0]} +{_descriptions.Count - 1} more",
            };

            return Math.Abs(CostDelta) < 0.0001
                ? summary
                : $"{summary}, {(CostDelta > 0 ? "+" : "−")}{Format.Cost(Math.Abs(CostDelta))}";
        }
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    private static int LineOf(List<int> lineStarts, int offset)
    {
        var index = lineStarts.BinarySearch(offset);
        return index >= 0 ? index : Math.Max(0, ~index - 1);
    }
}
