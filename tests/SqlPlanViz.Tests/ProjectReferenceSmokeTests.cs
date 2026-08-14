using SqlPlanViz.Parsing;
using Xunit;

namespace SqlPlanViz.Tests;

public class ProjectReferenceSmokeTests
{
    [Theory]
    [InlineData(SampleLoader.OrdersActual)]
    [InlineData(SampleLoader.NestedLoopLookupStorm)]
    public void SampleLoaderLoadsAndParsesEmbeddedFixture(string sampleFileName)
    {
        var xml = SampleLoader.Load(sampleFileName);
        var plan = ShowplanParser.Parse(xml, sampleFileName);

        Assert.NotEmpty(plan.Statements);
    }
}
