using System.Text;
using System.Text.RegularExpressions;
using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics.Rules;

/// <summary>
/// Key Lookup / RID Lookup with high <see cref="PlanNode.ActualExecutions"/> under a Nested
/// Loop (tuning-roadmap.md Phase 2.5). Nested Loops runs the inner side once per outer row,
/// so a lookup there is executed once per matched row on the outer side — often the single
/// most expensive thing in the plan. Fix: a covering index built from the lookup's
/// <see cref="PlanNode.OutputList"/> (the columns it fetches) plus the join key columns
/// pulled from its own seek predicate.
/// </summary>
public sealed class KeyLookupStormRule : IPlanRule
{
    private const int HighExecutionThreshold = 100;

    public string RuleId => "key-lookup-storm";

    public IEnumerable<PlanFinding> Analyse(PlanStatement statement)
    {
        var totalCost = statement.Summary.TotalSubtreeCost;

        foreach (var (parent, node) in WalkWithParent(statement.Root))
        {
            if (node.PhysicalOp is not ("Key Lookup" or "RID Lookup"))
            {
                continue;
            }

            if (parent is null || parent.PhysicalOp != "Nested Loops")
            {
                continue;
            }

            if (node.ActualExecutions is not int executions || executions < HighExecutionThreshold)
            {
                continue;
            }

            var impact = totalCost > 0 ? Math.Clamp(node.EstimatedSubtreeCost / totalCost, 0, 1) : 0;
            var target = node.ObjectName ?? "the table";

            yield return new PlanFinding
            {
                RuleId = RuleId,
                Title = $"Key Lookup storm on {target} — {executions:N0} executions",
                Severity = FindingSeverity.Critical,
                Confidence = FindingConfidence.High,
                Nodes = [node],
                Why = "Nested Loops runs the inner side once per outer row. Here that is "
                      + $"{executions:N0} executions of a {node.PhysicalOp}"
                      + (impact > 0 ? $", which is {impact * 100:0.#}% of this query's cost" : string.Empty)
                      + ".",
                Fixes = BuildFixes(node),
                ImpactFraction = impact,
            };
        }
    }

    private static IReadOnlyList<Fix> BuildFixes(PlanNode lookup)
    {
        var (schema, table) = ParseSchemaTable(lookup.ObjectName);
        if (string.IsNullOrEmpty(table))
        {
            return
            [
                new Fix(
                    "Add a covering index on the lookup table's join key, including the columns this lookup fetches.",
                    null,
                    "Covering indexes speed up reads but cost extra storage and write time.",
                    FixKind.Index),
            ];
        }

        var keyColumns = ExtractKeyColumns(lookup.SeekPredicate);
        var includeColumns = lookup.OutputList
            .Select(StripQualifier)
            .Where(c => !string.IsNullOrEmpty(c) && !keyColumns.Contains(c, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (keyColumns.Count == 0)
        {
            return
            [
                new Fix(
                    $"Add a covering index on {schema}.{table} keyed on the join column(s), including: "
                    + string.Join(", ", includeColumns),
                    null,
                    "Could not determine the exact join key from this plan's seek predicate — verify the key column(s) before creating the index. Covering indexes cost extra storage and write time.",
                    FixKind.Investigate),
            ];
        }

        var snippet = BuildCreateIndex(schema, table, keyColumns, includeColumns);
        return
        [
            new Fix(
                $"Create a covering index on {schema}.{table} so this lookup is no longer needed.",
                snippet,
                "Adds write cost to every insert/update/delete that touches these columns — check this table's write volume first.",
                FixKind.Index),
        ];
    }

    private static List<string> ExtractKeyColumns(string? seekPredicate)
    {
        if (string.IsNullOrEmpty(seekPredicate))
        {
            return [];
        }

        // Seek predicates read like "[Db].[schema].[Table].[Column] as [alias].[Column] = …" —
        // pull the column name immediately preceding a comparison operator.
        var matches = Regex.Matches(seekPredicate, @"\[(\w+)\]\s*(?:=|>=|<=|>|<)");
        return matches
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string StripQualifier(string column)
    {
        var idx = column.LastIndexOf('.');
        return idx >= 0 ? column[(idx + 1)..] : column;
    }

    /// <summary>
    /// Reverses the format built by ShowplanParser.ParseObjectName: "schema.table[ AS
    /// alias][.index]". Best-effort — used only to label the generated DDL.
    /// </summary>
    private static (string Schema, string Table) ParseSchemaTable(string? objectName)
    {
        if (string.IsNullOrEmpty(objectName))
        {
            return (string.Empty, string.Empty);
        }

        var beforeAs = objectName.Split(" AS ", 2)[0];
        var parts = beforeAs.Split('.');
        return parts.Length > 1 ? (parts[0], parts[1]) : (string.Empty, parts[0]);
    }

    private static string BuildCreateIndex(
        string schema, string table, IReadOnlyList<string> keys, IReadOnlyList<string> included)
    {
        var indexName = $"IX_{table}_{string.Join("_", keys.Take(3))}";
        if (indexName.Length > 120)
        {
            indexName = indexName[..120];
        }

        var sb = new StringBuilder();
        sb.AppendLine($"CREATE NONCLUSTERED INDEX [{indexName}]");
        sb.Append($"ON [{schema}].[{table}] (");
        sb.Append(string.Join(", ", keys.Select(k => $"[{k}]")));
        sb.Append(')');

        if (included.Count > 0)
        {
            sb.AppendLine();
            sb.Append("INCLUDE (");
            sb.Append(string.Join(", ", included.Select(c => $"[{c}]")));
            sb.Append(')');
        }

        sb.Append(';');
        return sb.ToString();
    }

    private static IEnumerable<(PlanNode? Parent, PlanNode Node)> WalkWithParent(PlanNode root)
    {
        yield return (null, root);
        foreach (var pair in WalkChildren(root))
        {
            yield return pair;
        }

        static IEnumerable<(PlanNode? Parent, PlanNode Node)> WalkChildren(PlanNode node)
        {
            foreach (var child in node.Children)
            {
                yield return (node, child);
                foreach (var pair in WalkChildren(child))
                {
                    yield return pair;
                }
            }
        }
    }
}
