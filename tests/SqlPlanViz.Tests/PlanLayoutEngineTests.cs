using SqlPlanViz.Layout;
using SqlPlanViz.Parsing;
using Xunit;

namespace SqlPlanViz.Tests;

/// <summary>
/// Guards the Phase 1 node-height increase (hot-path-plan.md): depth spacing must scale with
/// NodeHeight so a taller card never overlaps the row below it, and every node in a layout
/// must get the same height (the engine has no per-node variance).
/// </summary>
public class PlanLayoutEngineTests
{
    [Theory]
    [InlineData(SampleLoader.OrdersActual)]
    [InlineData(SampleLoader.NestedLoopLookupStorm)]
    public void AllNodesShareTheConfiguredHeight(string sampleFileName)
    {
        var plan = ShowplanParser.Parse(SampleLoader.Load(sampleFileName), sampleFileName);
        var engine = new PlanLayoutEngine();
        var layout = engine.Layout(plan.Statements[0].Root);

        Assert.All(layout.Nodes, n => Assert.Equal(engine.NodeHeight, n.Height));
    }

    [Fact]
    public void DepthSpacingNeverLetsCardsOverlap()
    {
        var plan = ShowplanParser.Parse(SampleLoader.Load(SampleLoader.OrdersActual));
        var engine = new PlanLayoutEngine();
        var layout = engine.Layout(plan.Statements[0].Root);

        var byDepth = layout.Nodes.GroupBy(n => n.Depth).OrderBy(g => g.Key).ToList();
        for (var i = 1; i < byDepth.Count; i++)
        {
            var shallowerBottom = byDepth[i - 1].Max(n => n.Y) + engine.NodeHeight;
            var deeperTop = byDepth[i].Min(n => n.Y);
            Assert.True(
                deeperTop >= shallowerBottom,
                $"Depth {byDepth[i].Key} starts at {deeperTop} but depth {byDepth[i - 1].Key} cards extend to {shallowerBottom}.");
        }
    }
}
