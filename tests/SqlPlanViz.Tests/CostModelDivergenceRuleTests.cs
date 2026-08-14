using SqlPlanViz.Diagnostics;
using SqlPlanViz.Diagnostics.Rules;
using SqlPlanViz.Parsing;
using Xunit;

namespace SqlPlanViz.Tests;

public class CostModelDivergenceRuleTests
{
    [Fact]
    public void OrdersActual_RanksClusteredIndexScanAboveSortAboveParallelism()
    {
        var statement = ShowplanParser.Parse(SampleLoader.Load(SampleLoader.OrdersActual)).Statements[0];
        var rule = new CostModelDivergenceRule();

        var findings = rule.Analyse(statement).ToList();

        // Clustered Index Scan (NodeId 4): priced at ~83% of estimated cost but only
        // accounted for ~32% of elapsed time — by far the widest gap, and Critical.
        // Sort (NodeId 1) and Parallelism (NodeId 0) both land just over the 15pt threshold,
        // in that order. Hash Match and Index Seek stay under threshold — no finding.
        var nodeIds = findings.Select(f => f.Nodes.Single().NodeId).ToList();
        Assert.Equal([4, 1, 0], nodeIds);

        Assert.Equal(FindingSeverity.Critical, findings[0].Severity);
        Assert.True(findings[0].ImpactFraction > 0.4);

        // Ranked by the gap, descending — this rule's own contract.
        Assert.True(findings[0].ImpactFraction >= findings[1].ImpactFraction);
        Assert.True(findings[1].ImpactFraction >= findings[2].ImpactFraction);

        Assert.DoesNotContain(findings, f => f.Nodes.Single().NodeId is 2 or 3);
    }

    [Fact]
    public void EstimatedOnlyPlan_ProducesNoFindings()
    {
        var statement = ShowplanParser.Parse(SampleLoader.Load(SampleLoader.OrdersEstimated)).Statements[0];
        var rule = new CostModelDivergenceRule();

        Assert.Empty(rule.Analyse(statement));
    }
}
