using SqlPlanViz.Diagnostics;
using SqlPlanViz.Diagnostics.Rules;
using SqlPlanViz.Model;
using SqlPlanViz.Parsing;
using Xunit;

namespace SqlPlanViz.Tests;

/// <summary>
/// Locks in today's finding set for every rule against both samples, so a change in rule
/// behaviour fails loudly here rather than silently drifting (hot-path-plan.md Phase 0).
/// One method per <see cref="IPlanRule"/> in <see cref="RuleEngine"/>'s built-in list.
/// </summary>
public class RuleCharacterisationTests
{
    private static PlanStatement OrdersActual() =>
        ShowplanParser.Parse(SampleLoader.Load(SampleLoader.OrdersActual)).Statements[0];

    private static PlanStatement LookupStorm() =>
        ShowplanParser.Parse(SampleLoader.Load(SampleLoader.NestedLoopLookupStorm)).Statements[0];

    private static void AssertTitles(IPlanRule rule, PlanStatement statement, params string[] expectedTitles)
    {
        var titles = rule.Analyse(statement).Select(f => f.Title).ToList();
        Assert.Equal(expectedTitles, titles);
    }

    [Fact]
    public void EstimateBlowupOrigin()
    {
        var rule = new EstimateBlowupOriginRule();
        AssertTitles(rule, OrdersActual(),
            "Row estimate first goes wrong here — 206.4x under at the dbo.Orders AS o.PK_Orders");
        AssertTitles(rule, LookupStorm(),
            "Row estimate first goes wrong here — 10000x under at the dbo.Orders AS o.IX_Orders_CustomerId");
    }

    [Fact]
    public void KeyLookupStorm()
    {
        var rule = new KeyLookupStormRule();
        AssertTitles(rule, OrdersActual());
        AssertTitles(rule, LookupStorm(),
            "Key Lookup storm on dbo.Orders AS o.PK_Orders — 50,000 executions");
    }

    [Fact]
    public void ResidualPredicateScan()
    {
        var rule = new ResidualPredicateScanRule();
        AssertTitles(rule, OrdersActual());
        AssertTitles(rule, LookupStorm());
    }

    [Fact]
    public void ImplicitConversion()
    {
        var rule = new ImplicitConversionRule();
        AssertTitles(rule, OrdersActual());
        AssertTitles(rule, LookupStorm(),
            "Implicit conversion on CONVERT_IMPLICIT(int,[@CustomerId],0)");
    }

    [Fact]
    public void Spill()
    {
        var rule = new SpillRule();
        AssertTitles(rule, OrdersActual(), "Sort spilled to tempdb");
        AssertTitles(rule, LookupStorm(), "Sort spilled to tempdb");
    }

    [Fact]
    public void NonSargablePredicate()
    {
        var rule = new NonSargablePredicateRule();
        AssertTitles(rule, OrdersActual());
        AssertTitles(rule, LookupStorm());
    }

    [Fact]
    public void ParameterSniffing()
    {
        var rule = new ParameterSniffingRule();
        AssertTitles(rule, OrdersActual());
        AssertTitles(rule, LookupStorm(),
            "Parameter sniffing likely — @CustomerId compiled for a different value than it ran with");
    }

    [Fact]
    public void ParallelismSkew()
    {
        var rule = new ParallelismSkewRule();
        AssertTitles(rule, OrdersActual());
        AssertTitles(rule, LookupStorm());
    }

    [Fact]
    public void StaleStatistics()
    {
        var rule = new StaleStatisticsRule();
        AssertTitles(rule, OrdersActual());
        AssertTitles(rule, LookupStorm(),
            "Stale statistics on dbo.Orders.IX_Orders_CustomerId — 812,400 modifications since last update");
    }

    [Fact]
    public void OptimizerGaveUp()
    {
        var rule = new OptimizerGaveUpRule();
        AssertTitles(rule, OrdersActual());
        AssertTitles(rule, LookupStorm(),
            "Optimizer gave up early (TimeOut)");
    }

    [Fact]
    public void FatInnerSideLoop()
    {
        var rule = new FatInnerSideLoopRule();
        AssertTitles(rule, OrdersActual());
        AssertTitles(rule, LookupStorm(),
            "Expensive inner side of Nested Loop — dbo.Orders AS o.PK_Orders runs 50,000 times");
    }

    [Fact]
    public void SpoolTrap()
    {
        var rule = new SpoolTrapRule();
        AssertTitles(rule, OrdersActual());
        AssertTitles(rule, LookupStorm());
    }

    [Fact]
    public void ScalarUdf()
    {
        var rule = new ScalarUdfRule();
        AssertTitles(rule, OrdersActual());
        AssertTitles(rule, LookupStorm());
    }

    [Fact]
    public void WaitDominated()
    {
        var rule = new WaitDominatedRule();
        AssertTitles(rule, OrdersActual());
        AssertTitles(rule, LookupStorm(),
            "This is not a plan problem — most of the time was spent waiting");
    }

    [Fact]
    public void WideUpdate()
    {
        var rule = new WideUpdateRule();
        AssertTitles(rule, OrdersActual());
        AssertTitles(rule, LookupStorm());
    }

    [Fact]
    public void MissingIndexMerge()
    {
        var rule = new MissingIndexMergeRule();
        AssertTitles(rule, OrdersActual());
        AssertTitles(rule, LookupStorm(),
            "2 overlapping index suggestions on dbo.Orders can merge into one");
    }
}
