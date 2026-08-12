using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics;

public enum PlanDiffKind
{
    Unchanged,
    Added,
    Removed,
    Changed,
}

public sealed record PlanNodeDelta
{
    public PlanNode? Before { get; init; }

    public PlanNode? After { get; init; }

    public required PlanDiffKind Kind { get; init; }

    public double CostDelta { get; init; }

    public double? RowsDelta { get; init; }

    public string DisplayName => After?.PhysicalOp ?? Before?.PhysicalOp ?? "Operator";
}

public sealed record PlanDiffResult
{
    public required PlanStatement Before { get; init; }

    public required PlanStatement After { get; init; }

    public required IReadOnlyList<PlanNodeDelta> Nodes { get; init; }

    public bool HasChanges => Nodes.Any(n => n.Kind != PlanDiffKind.Unchanged);
}

/// <summary>Matches operators by subtree shape plus object name, then compares their metrics.</summary>
public static class PlanDiff
{
    public static PlanDiffResult Compare(PlanStatement before, PlanStatement after)
    {
        var beforeGroups = IndexByShape(before.Root);
        var afterGroups = IndexByShape(after.Root);
        var deltas = new List<PlanNodeDelta>();

        foreach (var key in beforeGroups.Keys.Union(afterGroups.Keys, StringComparer.Ordinal))
        {
            var beforeNodes = beforeGroups.GetValueOrDefault(key) ?? [];
            var afterNodes = afterGroups.GetValueOrDefault(key) ?? [];
            var paired = Math.Min(beforeNodes.Count, afterNodes.Count);

            for (var i = 0; i < paired; i++)
            {
                var oldNode = beforeNodes[i];
                var newNode = afterNodes[i];
                var costDelta = newNode.EstimatedOperatorCost - oldNode.EstimatedOperatorCost;
                double? rowsDelta = newNode.ActualRows is double newRows && oldNode.ActualRows is double oldRows
                    ? newRows - oldRows
                    : null;
                var changed = MateriallyDifferent(oldNode.EstimatedOperatorCost, newNode.EstimatedOperatorCost, 0.20)
                              || (oldNode.ActualRows is double a && newNode.ActualRows is double b && MateriallyDifferent(a, b, 0.50));

                deltas.Add(new PlanNodeDelta
                {
                    Before = oldNode,
                    After = newNode,
                    Kind = changed ? PlanDiffKind.Changed : PlanDiffKind.Unchanged,
                    CostDelta = costDelta,
                    RowsDelta = rowsDelta,
                });
            }

            for (var i = paired; i < beforeNodes.Count; i++)
            {
                deltas.Add(new PlanNodeDelta { Before = beforeNodes[i], Kind = PlanDiffKind.Removed });
            }

            for (var i = paired; i < afterNodes.Count; i++)
            {
                deltas.Add(new PlanNodeDelta { After = afterNodes[i], Kind = PlanDiffKind.Added });
            }
        }

        return new PlanDiffResult { Before = before, After = after, Nodes = deltas };
    }

    private static Dictionary<string, List<PlanNode>> IndexByShape(PlanNode root)
    {
        var result = new Dictionary<string, List<PlanNode>>(StringComparer.Ordinal);

        string Visit(PlanNode node)
        {
            var childShapes = node.Children.Select(Visit);
            var shape = $"{node.PhysicalOp}|{NormalizeObject(node.ObjectName)}|({string.Join(',', childShapes)})";
            if (!result.TryGetValue(shape, out var matches))
            {
                matches = [];
                result[shape] = matches;
            }

            matches.Add(node);
            return shape;
        }

        Visit(root);
        return result;
    }

    private static string NormalizeObject(string? objectName) =>
        (objectName ?? string.Empty).Replace("[", string.Empty).Replace("]", string.Empty).ToUpperInvariant();

    private static bool MateriallyDifferent(double before, double after, double threshold)
    {
        var baseline = Math.Max(Math.Abs(before), 0.000001);
        return Math.Abs(after - before) / baseline >= threshold;
    }
}
