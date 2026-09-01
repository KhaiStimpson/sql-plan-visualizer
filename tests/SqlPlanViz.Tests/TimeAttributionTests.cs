using SqlPlanViz.Model;
using SqlPlanViz.Parsing;
using Xunit;

namespace SqlPlanViz.Tests;

public class TimeAttributionTests
{
    [Fact]
    public void SerialPlan_ChildrenSumToRootElapsed_WithinTolerance()
    {
        // nested-loop-lookup-storm.sqlplan runs at DegreeOfParallelism=1 — no Parallel nodes.
        var statement = ShowplanParser.Parse(SampleLoader.Load(SampleLoader.NestedLoopLookupStorm)).Statements[0];

        var result = TimeAttribution.Build(statement, TimeAttributionBasis.Elapsed);

        Assert.All(result.Frames, f => Assert.False(f.IsApproximate));

        var root = result.Frames.Single(f => f.Depth == 0);
        Assert.Equal(statement.Root.ActualElapsedMs!.Value, root.Width, precision: 3);

        var topLevelChildren = result.Frames.Where(f => f.Depth == 1).ToList();
        var childrenSpan = topLevelChildren.Sum(f => f.Width);
        Assert.True(
            childrenSpan <= root.Width + 0.01,
            $"Children span {childrenSpan}ms exceeds root width {root.Width}ms — over-counted.");
    }

    [Fact]
    public void ParallelPlan_ElapsedFramesBelowParallelismAreFlaggedApproximate()
    {
        // orders-actual.sqlplan runs at DegreeOfParallelism=4 with a Parallelism/Gather Streams root.
        var statement = ShowplanParser.Parse(SampleLoader.Load(SampleLoader.OrdersActual)).Statements[0];

        var result = TimeAttribution.Build(statement, TimeAttributionBasis.Elapsed);

        var root = result.Frames.Single(f => f.Depth == 0);
        Assert.False(root.IsApproximate); // the Parallelism node itself is the boundary, not below it

        var belowParallelism = result.Frames.Where(f => f.Depth >= 1).ToList();
        Assert.NotEmpty(belowParallelism);
        Assert.All(belowParallelism, f => Assert.True(f.IsApproximate));
    }

    [Fact]
    public void ParallelPlan_CpuBasisIsNeverApproximate()
    {
        var statement = ShowplanParser.Parse(SampleLoader.Load(SampleLoader.OrdersActual)).Statements[0];

        var result = TimeAttribution.Build(statement, TimeAttributionBasis.Cpu);

        Assert.All(result.Frames, f => Assert.False(f.IsApproximate));
    }

    [Fact]
    public void EstimatedOnlyPlan_ProducesNoFrames()
    {
        var statement = ShowplanParser.Parse(SampleLoader.Load(SampleLoader.OrdersEstimated)).Statements[0];

        var result = TimeAttribution.Build(statement, TimeAttributionBasis.Elapsed);

        Assert.Empty(result.Frames);
        Assert.Equal(0, result.ClampedNegativeSelfCount);
    }

    [Fact]
    public void RowsReadBasis_UsesActualRows()
    {
        var statement = ShowplanParser.Parse(SampleLoader.Load(SampleLoader.NestedLoopLookupStorm)).Statements[0];

        var result = TimeAttribution.Build(statement, TimeAttributionBasis.RowsRead);

        var root = result.Frames.Single(f => f.Depth == 0);
        Assert.Equal(statement.Root.ActualRows!.Value, root.Width);
    }

    [Fact]
    public void FramesAreOrderedByExecutionAndCoverEveryNode()
    {
        var statement = ShowplanParser.Parse(SampleLoader.Load(SampleLoader.OrdersActual)).Statements[0];

        var result = TimeAttribution.Build(statement, TimeAttributionBasis.Cpu);

        Assert.Equal(statement.AllNodes.Count, result.Frames.Count);
        Assert.Equal(
            statement.AllNodes.Select(n => n.NodeId).OrderBy(id => id),
            result.Frames.Select(f => f.NodeId).OrderBy(id => id));
    }
}
