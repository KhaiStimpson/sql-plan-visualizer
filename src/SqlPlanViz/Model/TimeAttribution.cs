namespace SqlPlanViz.Model;

/// <summary>Which counter drives a <see cref="TimeAttributionFrame"/>'s width (hot-path-plan.md Phase 2).</summary>
public enum TimeAttributionBasis
{
    /// <summary><see cref="PlanNode.ActualElapsedMs"/> — wall clock, inclusive of children. Not additive under parallelism.</summary>
    Elapsed,

    /// <summary><see cref="PlanNode.ActualCpuMs"/> — CPU-milliseconds, inclusive of children but conserved regardless of concurrency, so this is the reliable additive basis under a parallel operator.</summary>
    Cpu,

    /// <summary><see cref="PlanNode.ActualRows"/> — rows this operator actually produced.</summary>
    RowsRead,
}

/// <summary>
/// One node's slot in the flattened flame layout. <see cref="Offset"/> and <see cref="Width"/>
/// share the same unit as <see cref="Basis"/> (ms or rows) — a child's <c>[Offset, Offset +
/// Width)</c> range sits inside its parent's, in execution order; whatever the parent's width
/// doesn't cover is that node's own self-time/self-rows, shown as empty space rather than a
/// separate frame.
/// </summary>
public sealed record TimeAttributionFrame(
    int NodeId,
    int Depth,
    double Offset,
    double Width,
    TimeAttributionBasis Basis,
    bool IsApproximate);

/// <summary>
/// <see cref="Frames"/> plus a count of nodes where children's combined width exceeded their
/// parent's own width — over-subscription that only clamps to zero rather than throwing, but
/// that the view must disclose (hot-path-plan.md Phase 2): a non-zero
/// <see cref="ClampedNegativeSelfCount"/> means this basis is unreliable for this plan.
/// </summary>
public sealed record TimeAttributionResult(
    IReadOnlyList<TimeAttributionFrame> Frames,
    int ClampedNegativeSelfCount);

/// <summary>
/// Flattens a <see cref="PlanStatement"/> into ordered flame-graph frames. Pure derivation, no
/// UI references — <c>Views/FlameView</c> consumes this, it doesn't compute it.
/// </summary>
public static class TimeAttribution
{
    public static TimeAttributionResult Build(PlanStatement statement, TimeAttributionBasis basis)
    {
        var frames = new List<TimeAttributionFrame>();
        var clamped = 0;

        if (statement.HasRuntimeStats)
        {
            Visit(statement.Root, depth: 0, offset: 0, ancestorParallel: false, basis, frames, ref clamped);
        }

        return new TimeAttributionResult(frames, clamped);
    }

    private static double Width(PlanNode node, TimeAttributionBasis basis) => basis switch
    {
        TimeAttributionBasis.Elapsed => node.ActualElapsedMs ?? 0,
        TimeAttributionBasis.Cpu => node.ActualCpuMs ?? 0,
        TimeAttributionBasis.RowsRead => node.ActualRows ?? 0,
        _ => 0,
    };

    private static void Visit(
        PlanNode node,
        int depth,
        double offset,
        bool ancestorParallel,
        TimeAttributionBasis basis,
        List<TimeAttributionFrame> frames,
        ref int clamped)
    {
        var width = Width(node, basis);

        // Elapsed time under a parallel operator is a max-across-threads figure, so sibling
        // widths laid out end-to-end don't actually partition the parent's span — the layout
        // is still drawn, but flagged. CPU and row counts are conserved regardless of
        // concurrency, so they're never approximate.
        var isApproximate = basis == TimeAttributionBasis.Elapsed && ancestorParallel;

        frames.Add(new TimeAttributionFrame(node.NodeId, depth, offset, width, basis, isApproximate));

        var childrenTotal = node.Children.Sum(c => Width(c, basis));
        if (childrenTotal > width)
        {
            // This node's implicit self-share (width minus children) would be negative —
            // measurement noise or a basis this shape doesn't suit. Clamped to zero; the
            // frame widths themselves are left alone since each still reflects real data.
            clamped++;
        }

        var childOffset = offset;
        var childAncestorParallel = ancestorParallel || node.Parallel;
        foreach (var child in node.Children)
        {
            Visit(child, depth + 1, childOffset, childAncestorParallel, basis, frames, ref clamped);
            childOffset += Width(child, basis);
        }
    }
}
