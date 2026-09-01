using SqlPlanViz.Diagnostics;
using SqlPlanViz.Model;
using SqlPlanViz.Parsing;
using Xunit;

namespace SqlPlanViz.Tests;

public class NodeLabellerTests
{
    private static PlanNode Find(PlanStatement statement, int nodeId) =>
        statement.AllNodes.Single(n => n.NodeId == nodeId);

    [Fact]
    public void DescribeSources_HashMatchJoin_NamesBothSidesInOrdersActual()
    {
        var statement = ShowplanParser.Parse(SampleLoader.Load(SampleLoader.OrdersActual)).Statements[0];
        var hashMatch = Find(statement, 2); // Hash Match / Inner Join

        Assert.Equal("Customers ⋈ Orders", NodeLabeller.DescribeSources(hashMatch));
    }

    [Fact]
    public void DescribeSources_NestedLoops_NamesBothSidesInLookupStorm()
    {
        var statement = ShowplanParser.Parse(SampleLoader.Load(SampleLoader.NestedLoopLookupStorm)).Statements[0];
        var nestedLoops = Find(statement, 1); // Nested Loops / Inner Join

        Assert.Equal("Orders ⋈ Orders", NodeLabeller.DescribeSources(nestedLoops));
    }

    [Fact]
    public void DescribeSources_NodeWithOwnObject_ReturnsNull()
    {
        var statement = ShowplanParser.Parse(SampleLoader.Load(SampleLoader.OrdersActual)).Statements[0];
        var indexSeek = Find(statement, 3); // Index Seek on Customers — has its own object

        Assert.Null(NodeLabeller.DescribeSources(indexSeek));
    }

    [Fact]
    public void DescribeSources_ZeroSources_ReturnsNull()
    {
        var node = new PlanNode { PhysicalOp = "Constant Scan", LogicalOp = "Constant Scan" };

        Assert.Null(NodeLabeller.DescribeSources(node));
    }

    [Fact]
    public void DescribeSources_ThreeOrMoreSources_ReportsCount()
    {
        var node = new PlanNode
        {
            PhysicalOp = "Concatenation",
            LogicalOp = "Concatenation",
            Children = [Leaf("A"), Leaf("B"), Leaf("C")],
        };

        Assert.Equal("3 sources", NodeLabeller.DescribeSources(node));
    }

    [Fact]
    public void DescribeSources_PassesThroughAmbiguousShapes()
    {
        // Nested Loops -> [Table Spool wrapping Orders, Parallelism wrapping Customers]
        var node = new PlanNode
        {
            PhysicalOp = "Nested Loops",
            LogicalOp = "Inner Join",
            Children =
            [
                new PlanNode { PhysicalOp = "Table Spool", LogicalOp = "Lazy Spool", Children = [Leaf("Orders")] },
                new PlanNode { PhysicalOp = "Parallelism", LogicalOp = "Gather Streams", Children = [Leaf("Customers")] },
            ],
        };

        Assert.Equal("Orders ⋈ Customers", NodeLabeller.DescribeSources(node));
    }

    private static PlanNode Leaf(string table) =>
        new() { PhysicalOp = "Clustered Index Scan", LogicalOp = "Clustered Index Scan", ObjectTable = table };

    [Fact]
    public void DescribeJoinKeys_MatchingColumnNames_ReturnsShortForm()
    {
        var node = new PlanNode
        {
            PhysicalOp = "Merge Join",
            LogicalOp = "Inner Join",
            Predicate = "[SalesDb].[dbo].[Customers].[CustomerId] as [c].[CustomerId] = " +
                        "[SalesDb].[dbo].[Orders].[CustomerId] as [o].[CustomerId]",
        };

        Assert.Equal("on CustomerId", NodeLabeller.DescribeJoinKeys(node));
    }

    [Fact]
    public void DescribeJoinKeys_DifferingColumnNames_ReturnsBothSides()
    {
        var node = new PlanNode
        {
            PhysicalOp = "Merge Join",
            LogicalOp = "Inner Join",
            Predicate = "[SalesDb].[dbo].[Orders].[CustomerId] as [o].[CustomerId] = " +
                        "[SalesDb].[dbo].[Customers].[ParentCustomerId] as [p].[ParentCustomerId]",
        };

        Assert.Equal("on CustomerId = ParentCustomerId", NodeLabeller.DescribeJoinKeys(node));
    }

    [Fact]
    public void DescribeJoinKeys_NonEqualityPredicate_ReturnsNull()
    {
        var node = new PlanNode
        {
            PhysicalOp = "Merge Join",
            LogicalOp = "Inner Join",
            Predicate = "datepart(YEAR,[SalesDb].[dbo].[Orders].[OrderDate] as [o].[OrderDate])=(2024)",
        };

        Assert.Null(NodeLabeller.DescribeJoinKeys(node));
    }

    [Fact]
    public void DescribeJoinKeys_NonJoinNode_ReturnsNull()
    {
        var node = new PlanNode
        {
            PhysicalOp = "Filter",
            LogicalOp = "Filter",
            Predicate = "[SalesDb].[dbo].[Orders].[CustomerId] as [o].[CustomerId] = [SalesDb].[dbo].[Orders].[CustomerId] as [o].[CustomerId]",
        };

        Assert.Null(NodeLabeller.DescribeJoinKeys(node));
    }

    [Fact]
    public void DescribeJoinKeys_BothRealSamples_HaveNoPredicateOnTheJoinNodeItself()
    {
        // Documents the known limitation: real Showplan usually carries the join key on a
        // child SeekPredicate/HashKeys element, not on the join RelOp's own Predicate.
        var actual = ShowplanParser.Parse(SampleLoader.Load(SampleLoader.OrdersActual)).Statements[0];
        var storm = ShowplanParser.Parse(SampleLoader.Load(SampleLoader.NestedLoopLookupStorm)).Statements[0];

        Assert.Null(NodeLabeller.DescribeJoinKeys(Find(actual, 2)));
        Assert.Null(NodeLabeller.DescribeJoinKeys(Find(storm, 1)));
    }

    [Theory]
    [InlineData("dbo.Orders AS o.PK_Orders", 32, "dbo.Orders AS o.PK_Orders")]
    [InlineData("dbo.SomeVeryLongSchemaNameHere AS o.IX_SomeVeryLongIndexName", 24, "…X_SomeVeryLongIndexName")]
    public void TruncateObjectName_KeepsDistinguishingSuffix(string input, int maxLength, string expected)
    {
        Assert.Equal(expected, NodeLabeller.TruncateObjectName(input, maxLength));
    }
}
