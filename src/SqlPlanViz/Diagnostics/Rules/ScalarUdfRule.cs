using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics.Rules;

/// <summary>
/// A UDF operator, or a TVF with the giveaway fixed 100/1 row estimate that pre-inlining SQL
/// Server always produces for a multi-statement table-valued function
/// (tuning-roadmap.md Phase 3.7). Both are cardinality-estimation black holes and, for
/// scalar UDFs, force row-by-row execution instead of set-based processing.
/// </summary>
public sealed class ScalarUdfRule : IPlanRule
{
    public string RuleId => "scalar-udf";

    public IEnumerable<PlanFinding> Analyse(PlanStatement statement)
    {
        var totalCost = statement.Summary.TotalSubtreeCost;

        foreach (var node in statement.AllNodes)
        {
            var isUdf = node.PhysicalOp.Contains("UDF", StringComparison.OrdinalIgnoreCase)
                        || node.LogicalOp.Contains("UDF", StringComparison.OrdinalIgnoreCase);

            // A multi-statement TVF that SQL Server hasn't inlined always estimates a fixed
            // 100 rows (or 1, depending on version) regardless of the function's actual
            // output — the classic giveaway that inlining didn't happen.
            var looksLikeUninlinedTvf = !isUdf
                && node.PhysicalOp.Contains("Table Valued Function", StringComparison.OrdinalIgnoreCase)
                && (node.EstimatedRows is 100 or 1);

            if (!isUdf && !looksLikeUninlinedTvf)
            {
                continue;
            }

            var impact = totalCost > 0 ? Math.Clamp(node.EstimatedSubtreeCost / totalCost, 0, 1) : 0;
            var target = node.ObjectName ?? node.PhysicalOp;

            yield return new PlanFinding
            {
                RuleId = RuleId,
                Title = isUdf ? $"Scalar UDF at {target}" : $"Un-inlined table-valued function at {target}",
                Severity = FindingSeverity.Warning,
                Confidence = isUdf ? FindingConfidence.High : FindingConfidence.Likely,
                Nodes = [node],
                Why = isUdf
                    ? $"{target} invokes a user-defined function. Unless it qualifies for scalar UDF "
                      + "inlining (SQL Server 2019+, and only for functions simple enough to inline), the "
                      + "optimizer executes it once per row instead of as part of a set-based plan, and "
                      + "cannot estimate its cost or selectivity."
                    : $"{target} estimates a fixed {node.EstimatedRows:0} row(s) — the signature of a "
                      + "multi-statement table-valued function that was not inlined. Every downstream "
                      + "operator's cardinality estimate is built on this fabricated number.",
                Fixes =
                [
                    new Fix(
                        "Inline the function's logic directly into the query, or rewrite it as an inline table-valued function (a single RETURN SELECT).",
                        null,
                        null,
                        FixKind.Rewrite),
                    new Fix(
                        "If on SQL Server 2019+, check why scalar UDF inlining didn't apply (TSQL feature use, or WITH SCHEMABINDING requirements not met).",
                        null,
                        null,
                        FixKind.Investigate),
                ],
                ImpactFraction = impact,
            };
        }
    }
}
