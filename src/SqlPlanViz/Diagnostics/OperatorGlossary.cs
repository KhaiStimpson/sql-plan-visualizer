using SqlPlanViz.Common;
using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics;

public enum ExplanationVerbosity
{
    Terse,
    Expansive,
}

/// <summary>
/// Plain-language operator explanations. The glossary owns operator semantics while the
/// caller supplies a node so every explanation can finish with that operator's evidence.
/// </summary>
public static class OperatorGlossary
{
    private static readonly IReadOnlyDictionary<string, string> Descriptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Adaptive Join"] = "An Adaptive Join chooses between a Nested Loops and Hash Match strategy at runtime, based on how many rows reach it.",
            ["Assert"] = "An Assert checks a condition such as a constraint or scalar-subquery row limit and stops the query if it is violated.",
            ["Bitmap"] = "A Bitmap builds a compact filter that lets another operator discard non-matching rows cheaply, often before a parallel hash join.",
            ["Clustered Index Delete"] = "A Clustered Index Delete removes rows from the table's clustered storage and maintains affected indexes.",
            ["Clustered Index Insert"] = "A Clustered Index Insert writes rows into the table's clustered storage and maintains affected indexes.",
            ["Clustered Index Scan"] = "A Clustered Index Scan reads a range or all of the table's clustered index because no narrower access path was chosen.",
            ["Clustered Index Seek"] = "A Clustered Index Seek navigates the clustered index to a key or range, avoiding a full read of the table.",
            ["Clustered Index Update"] = "A Clustered Index Update changes rows in the table's clustered storage and maintains affected indexes.",
            ["Compute Scalar"] = "A Compute Scalar evaluates an expression for each row, such as a conversion, arithmetic expression, or generated value.",
            ["Concatenation"] = "A Concatenation appends rows from several inputs, commonly implementing UNION ALL or combining branches of a rewrite.",
            ["Constant Scan"] = "A Constant Scan creates one or more rows without reading a table, often for literal values or internal query scaffolding.",
            ["Filter"] = "A Filter tests a predicate after rows have already been produced and removes those that do not qualify.",
            ["Hash Match"] = "A Hash Match builds an in-memory hash table for a join, aggregate, or distinct operation and probes it with incoming rows.",
            ["Index Delete"] = "An Index Delete removes entries from a nonclustered index as part of a data modification.",
            ["Index Insert"] = "An Index Insert adds entries to a nonclustered index as part of a data modification.",
            ["Index Scan"] = "An Index Scan reads a range or all of a nonclustered index because SQL Server did not choose a narrower seek.",
            ["Index Seek"] = "An Index Seek navigates a nonclustered index to matching keys or ranges instead of reading the whole index.",
            ["Index Spool"] = "An Index Spool stores intermediate rows in a temporary indexed worktable so they can be searched again later in the plan.",
            ["Index Update"] = "An Index Update changes entries in a nonclustered index as part of a data modification.",
            ["Key Lookup"] = "A Key Lookup fetches columns missing from a nonclustered index by visiting the matching clustered row once per lookup.",
            ["Merge Join"] = "A Merge Join walks two ordered inputs together and can be very efficient when both sides already arrive in join-key order.",
            ["Nested Loops"] = "A Nested Loops join runs its inner input once for each qualifying row from its outer input.",
            ["Parallelism"] = "A Parallelism operator redistributes, gathers, or broadcasts rows between worker threads at a parallel-plan boundary.",
            ["RID Lookup"] = "A RID Lookup fetches columns missing from a nonclustered index by visiting a heap row once per lookup.",
            ["Segment"] = "A Segment marks boundaries between ordered groups so a following operator can calculate per-group results.",
            ["Sequence"] = "A Sequence runs its inputs in a defined order and returns rows from the final branch.",
            ["Sequence Project"] = "A Sequence Project computes ordered, row-by-row values such as ranking and window-function results.",
            ["Sort"] = "A Sort orders its entire input for an ORDER BY, merge operation, stream aggregate, or window function.",
            ["Stream Aggregate"] = "A Stream Aggregate consumes rows already ordered by the grouping keys and produces one result per group.",
            ["Table Delete"] = "A Table Delete removes rows from a heap and maintains affected indexes.",
            ["Table Insert"] = "A Table Insert writes rows into a heap and maintains affected indexes.",
            ["Table Scan"] = "A Table Scan reads a range or all rows from a heap because no suitable index access path was chosen.",
            ["Table Spool"] = "A Table Spool stores intermediate rows in a temporary worktable so another part of the plan can reuse them.",
            ["Table Update"] = "A Table Update changes rows in a heap and maintains affected indexes.",
            ["Table-valued function"] = "A Table-valued Function obtains rows by executing a function whose internal work may be hidden from this plan.",
            ["Top"] = "A Top stops requesting rows after its row goal is satisfied, which can change the optimizer's preferred access and join strategies.",
            ["Window Aggregate"] = "A Window Aggregate calculates aggregate values across an ordered window while preserving the input rows.",
            ["Window Spool"] = "A Window Spool stores rows needed to evaluate a window frame for each output row.",
        };

    public static OperatorExplanation Explain(PlanNode node, ExplanationVerbosity verbosity = ExplanationVerbosity.Expansive)
    {
        var description = Descriptions.TryGetValue(node.PhysicalOp, out var known)
            ? known
            : $"The {DisplayOperator(node.PhysicalOp)} operator performs the {DisplayOperator(node.LogicalOp)} step selected by SQL Server for this part of the plan.";

        return new OperatorExplanation(
            Title(node),
            description,
            RuntimeEvidence(node, verbosity));
    }

    private static string Title(PlanNode node) => string.IsNullOrWhiteSpace(node.ObjectName)
        ? node.PhysicalOp
        : $"{node.PhysicalOp} · {node.ObjectName}";

    private static string RuntimeEvidence(PlanNode node, ExplanationVerbosity verbosity)
    {
        var executions = Math.Max(node.ActualExecutions ?? 1, 1);
        var executionText = executions == 1 ? "once" : $"{Format.Rows(executions)} times";

        if (node.ActualRows is double actual)
        {
            var estimate = node.EstimatedRowsTotal;
            var timing = node.ActualElapsedMs is double elapsed
                ? $" in {Format.Milliseconds(elapsed)}"
                : string.Empty;
            var estimateText = node.EstimateErrorFactor is double factor && factor >= 1.5
                ? $", versus {Format.Rows(estimate)} estimated ({Format.Factor(factor)} off)"
                : $", versus {Format.Rows(estimate)} estimated";

            var evidence = $"Here it ran {executionText} and produced {Format.Rows(actual)} rows{timing}{estimateText}.";
            return verbosity == ExplanationVerbosity.Terse
                ? evidence
                : $"{evidence} Its estimated operator cost is {Format.Cost(node.EstimatedOperatorCost)}, "
                    + $"or {Format.Percent(node.EstimatedSubtreeCost <= 0 ? 0 : node.EstimatedOperatorCost / node.EstimatedSubtreeCost)} of this subtree's cost.";
        }

        var estimated = $"Here SQL Server estimates {Format.Rows(node.EstimatedRowsTotal)} rows across {Format.Rows(executions)} execution{(executions == 1 ? string.Empty : "s")}; runtime figures are not available for this plan.";
        return verbosity == ExplanationVerbosity.Terse
            ? estimated
            : $"{estimated} Its estimated operator cost is {Format.Cost(node.EstimatedOperatorCost)}.";
    }

    private static string DisplayOperator(string value) => string.IsNullOrWhiteSpace(value)
        ? "unknown"
        : value.ToLowerInvariant();
}

public sealed record OperatorExplanation(string Title, string Description, string Evidence);
