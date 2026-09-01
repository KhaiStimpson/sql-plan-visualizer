using SqlPlanViz.Parsing;
using Xunit;

namespace SqlPlanViz.Tests;

public class ProjectReferenceSmokeTests
{
    [Theory]
    [InlineData(SampleLoader.OrdersActual)]
    [InlineData(SampleLoader.NestedLoopLookupStorm)]
    [InlineData(SampleLoader.OrdersEstimated)]
    public void SampleLoaderLoadsAndParsesEmbeddedFixture(string sampleFileName)
    {
        var xml = SampleLoader.Load(sampleFileName);
        var plan = ShowplanParser.Parse(xml, sampleFileName);

        Assert.NotEmpty(plan.Statements);
    }

    [Fact]
    public void EstimatedOnlyFixtureHasNoRuntimeStats()
    {
        var xml = SampleLoader.Load(SampleLoader.OrdersEstimated);
        var plan = ShowplanParser.Parse(xml, SampleLoader.OrdersEstimated);

        Assert.False(plan.HasRuntimeStats);
        Assert.All(plan.Statements[0].AllNodes, node => Assert.Null(node.ActualRows));
    }
}
