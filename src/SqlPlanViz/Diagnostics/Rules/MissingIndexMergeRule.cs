using System.Text;
using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics.Rules;

/// <summary>
/// Dedupes and merges overlapping Showplan missing-index suggestions that share the same
/// key columns — e.g. <c>(A) INCLUDE (B)</c> + <c>(A) INCLUDE (C)</c> becomes one index
/// covering both (tuning-roadmap.md Phase 3.10). Always attaches the write-cost caveat and
/// the resulting index's column count; never presents a DMV suggestion as unqualified
/// advice.
/// </summary>
public sealed class MissingIndexMergeRule : IPlanRule
{
    public string RuleId => "missing-index-merge";

    public IEnumerable<PlanFinding> Analyse(PlanStatement statement)
    {
        var groups = statement.MissingIndexes
            .GroupBy(m => (m.Database, m.Schema, m.Table,
                Equality: string.Join(",", m.EqualityColumns),
                Inequality: string.Join(",", m.InequalityColumns)))
            .Where(g => g.Count() > 1)
            .ToList();

        if (groups.Count == 0)
        {
            return [];
        }

        var totalCost = statement.Summary.TotalSubtreeCost;
        var findings = new List<PlanFinding>();

        foreach (var group in groups)
        {
            var suggestions = group.ToList();
            var equality = suggestions[0].EqualityColumns;
            var inequality = suggestions[0].InequalityColumns;
            var included = suggestions
                .SelectMany(s => s.IncludedColumns)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var combinedImpact = suggestions.Sum(s => s.ImpactPercent);
            var target = suggestions[0].DisplayTarget;
            var width = equality.Count + inequality.Count + included.Count;

            var impact = totalCost > 0 ? Math.Clamp(combinedImpact / 100.0, 0, 1) : 0;

            findings.Add(new PlanFinding
            {
                RuleId = RuleId,
                Title = $"{suggestions.Count} overlapping index suggestions on {target} can merge into one",
                Severity = FindingSeverity.Info,
                Confidence = FindingConfidence.High,
                Nodes = [],
                Why = $"Showplan suggested {suggestions.Count} separate indexes on {target}, all keyed on the "
                      + $"same column(s) but with different INCLUDE lists. Creating them all wastes storage "
                      + $"and write time maintaining near-duplicate indexes; one {width}-column index covers "
                      + "every case they were suggested for.",
                Fixes =
                [
                    new Fix(
                        $"Create one merged index instead of {suggestions.Count} separate ones.",
                        BuildCreateIndex(group.Key.Schema, group.Key.Table, equality, inequality, included),
                        $"This is a DMV-style suggestion, not a verified recommendation — check it against "
                        + $"actual query patterns first. The resulting index has {width} column(s); wider "
                        + "indexes cost more to maintain on every write to this table.",
                        FixKind.Index),
                ],
                ImpactFraction = impact,
            });
        }

        return findings;
    }

    private static string BuildCreateIndex(
        string schema, string table,
        IReadOnlyList<string> equality, IReadOnlyList<string> inequality, IReadOnlyList<string> included)
    {
        var keys = equality.Concat(inequality).ToList();
        var nameParts = keys.Concat(included.Take(2)).Take(4);
        var indexName = $"IX_{Trim(table)}_{string.Join("_", nameParts.Select(Trim))}";
        if (indexName.Length > 120)
        {
            indexName = indexName[..120];
        }

        var sb = new StringBuilder();
        sb.AppendLine($"CREATE NONCLUSTERED INDEX [{indexName}]");
        sb.Append($"ON [{Trim(schema)}].[{Trim(table)}] (");
        sb.Append(string.Join(", ", keys.Select(k => $"[{Trim(k)}]")));
        sb.Append(')');

        if (included.Count > 0)
        {
            sb.AppendLine();
            sb.Append("INCLUDE (");
            sb.Append(string.Join(", ", included.Select(c => $"[{Trim(c)}]")));
            sb.Append(')');
        }

        sb.Append(';');
        return sb.ToString();
    }

    private static string Trim(string s) => s.Trim('[', ']');
}
