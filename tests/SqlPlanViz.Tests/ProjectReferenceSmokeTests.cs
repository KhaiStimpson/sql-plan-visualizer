using SqlPlanViz.Model;
using Xunit;

namespace SqlPlanViz.Tests;

public class ProjectReferenceSmokeTests
{
    [Fact]
    public void CanReferenceAppModelTypes()
    {
        var plan = new ExecutionPlan();

        Assert.NotNull(plan);
    }
}
