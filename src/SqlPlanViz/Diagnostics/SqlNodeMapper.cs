using System.Text.RegularExpressions;
using SqlPlanViz.Model;
using SqlPlanViz.Sql;

namespace SqlPlanViz.Diagnostics;

public sealed record SqlTextSpan(int Start, int Length, string Clause);

/// <summary>
/// Best-effort operator-to-SQL mapping. Showplan carries no source spans, so this scores every
/// clause of the statement against the evidence the operator does carry — the alias and table it
/// touches, the columns, parameters and literals in its predicates, the columns it outputs — and
/// highlights the clause that best explains it.
///
/// Scoring rather than first-match matters: a plan says <c>[Db].[dbo].[Orders].[CustomerId] as
/// [o].[CustomerId]</c>, and <c>CustomerId</c> alone appears in three clauses of a typical query.
/// The alias-qualified form (<c>o.CustomerId</c>), the parameter and the literal are what
/// actually pin down which one, so those weigh most.
///
/// When nothing matches and the operator has no clause-level meaning either — a Parallelism
/// exchange has no SQL to point at — this returns null rather than guessing. A confident wrong
/// highlight is worse than none.
/// </summary>
public static partial class SqlNodeMapper
{
    /// <summary>How much the operator's expected clause counts when the text itself is ambiguous.</summary>
    private const int PreferredClauseBonus = 3;

    private const int AliasColumnWeight = 4;
    private const int ParameterWeight = 4;
    private const int LiteralWeight = 4;
    private const int TableWeight = 3;
    private const int QualifiedColumnWeight = 3;
    private const int AliasWeight = 2;
    private const int NumberWeight = 2;
    private const int ColumnWeight = 1;
    private const int OutputColumnWeight = 1;

    /// <summary>An output list can be dozens of columns wide; it is a hint, not the whole vote.</summary>
    private const int MaxOutputClues = 12;

    private readonly record struct Clue(string Text, int Weight);

    private static readonly string[] JoinDirections = ["Left", "Right", "Full"];

    public static SqlTextSpan? Map(string sql, PlanNode node)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return null;
        }

        var regions = SqlClauseSplitter.Split(sql);
        if (regions.Count == 0)
        {
            return null;
        }

        var preferred = PreferredClause(node);

        // Every operator outputs the columns flowing through it, so an output list mostly echoes
        // the select list — evidence only where producing those columns is the operator's whole
        // job (a lookup, a compute scalar). Elsewhere it would outvote the real signals.
        var clues = CollectClues(node, includeOutputList: preferred == "SELECT");

        SqlClauseRegion? best = null;
        var bestScore = 0;

        foreach (var region in regions)
        {
            var score = 0;
            foreach (var clue in clues)
            {
                if (ContainsWord(region.SearchText, clue.Text))
                {
                    score += clue.Weight;
                }
            }

            var isPreferred = preferred is not null && region.Kind == preferred;
            if (score == 0 && !isPreferred)
            {
                continue;
            }

            var total = score + (isPreferred ? PreferredClauseBonus : 0);
            if (total > bestScore)
            {
                bestScore = total;
                best = region;
            }
        }

        return best is null ? null : new SqlTextSpan(best.Start, best.Length, best.Kind);
    }

    /// <summary>
    /// The clause an operator belongs to when the text gives no better answer. Operators that
    /// exist only to move rows around (exchanges, spools) deliberately map to nothing.
    /// </summary>
    private static string? PreferredClause(PlanNode node)
    {
        var physical = node.PhysicalOp;
        var logical = node.LogicalOp;

        if (logical.Contains("Aggregate", StringComparison.OrdinalIgnoreCase))
        {
            return "GROUP BY";
        }

        if (logical.Contains("Join", StringComparison.OrdinalIgnoreCase)
            || logical.Contains("Apply", StringComparison.OrdinalIgnoreCase))
        {
            return "JOIN";
        }

        if (logical.Contains("Union", StringComparison.OrdinalIgnoreCase)
            || physical.Contains("Concatenation", StringComparison.OrdinalIgnoreCase))
        {
            return "UNION";
        }

        if (physical.Contains("Sort", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER BY";
        }

        if (physical.Contains("Insert", StringComparison.OrdinalIgnoreCase))
        {
            return "INSERT";
        }

        if (physical.Contains("Update", StringComparison.OrdinalIgnoreCase))
        {
            return "UPDATE";
        }

        if (physical.Contains("Delete", StringComparison.OrdinalIgnoreCase))
        {
            return "DELETE";
        }

        if (physical.Contains("Filter", StringComparison.OrdinalIgnoreCase))
        {
            return "WHERE";
        }

        // A lookup exists to fetch the columns the index did not cover — that is the select list.
        if (physical.Contains("Lookup", StringComparison.OrdinalIgnoreCase)
            || physical.Contains("Compute Scalar", StringComparison.OrdinalIgnoreCase)
            || physical.Contains("Top", StringComparison.OrdinalIgnoreCase))
        {
            return "SELECT";
        }

        if (physical.Contains("Scan", StringComparison.OrdinalIgnoreCase)
            || physical.Contains("Seek", StringComparison.OrdinalIgnoreCase))
        {
            return "FROM";
        }

        return null;
    }

    private static List<Clue> CollectClues(PlanNode node, bool includeOutputList)
    {
        var clues = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Add(string? text, int weight)
        {
            if (string.IsNullOrWhiteSpace(text) || IsGeneratedName(text))
            {
                return;
            }

            var key = text.Trim();
            if (!clues.TryGetValue(key, out var existing) || weight > existing)
            {
                clues[key] = weight;
            }
        }

        Add(node.ObjectTable, TableWeight);
        if (!string.Equals(node.ObjectAlias, node.ObjectTable, StringComparison.OrdinalIgnoreCase))
        {
            Add(node.ObjectAlias, AliasWeight);
        }

        // "Left Outer Join" tells us which of several JOIN clauses this is, when the operator
        // carries no predicate of its own to match on.
        foreach (var direction in JoinDirections)
        {
            if (node.LogicalOp.StartsWith(direction, StringComparison.OrdinalIgnoreCase))
            {
                Add(direction, AliasWeight);
            }
        }

        foreach (var predicate in new[] { node.SeekPredicate, node.Predicate })
        {
            if (string.IsNullOrWhiteSpace(predicate))
            {
                continue;
            }

            // [Db].[dbo].[Orders].[OrderDate] as [o].[OrderDate] — the aliased half is the half
            // the query text actually contains.
            foreach (Match match in AliasedColumn().Matches(predicate))
            {
                Add($"{match.Groups[1].Value}.{match.Groups[2].Value}", AliasColumnWeight);
                Add(match.Groups[2].Value, ColumnWeight);
            }

            foreach (Match match in QualifiedName().Matches(predicate))
            {
                var parts = match.Value.Split('.');
                var column = parts[^1].Trim('[', ']');
                Add(column, ColumnWeight);
                if (parts.Length >= 2)
                {
                    Add($"{parts[^2].Trim('[', ']')}.{column}", QualifiedColumnWeight);
                }
            }

            foreach (Match match in Parameter().Matches(predicate))
            {
                Add(match.Groups[1].Value, ParameterWeight);
            }

            foreach (Match match in StringLiteral().Matches(predicate))
            {
                Add(match.Value, LiteralWeight);
            }

            foreach (Match match in NumericLiteral().Matches(predicate))
            {
                Add(match.Groups[1].Value, NumberWeight);
            }
        }

        if (includeOutputList)
        {
            foreach (var column in node.OutputList.Take(MaxOutputClues))
            {
                Add(column, OutputColumnWeight);
                var dot = column.LastIndexOf('.');
                if (dot >= 0)
                {
                    Add(column[(dot + 1)..], OutputColumnWeight);
                }
            }
        }

        return clues.Select(pair => new Clue(pair.Key.ToUpperInvariant(), pair.Value)).ToList();
    }

    /// <summary>
    /// Names the optimizer invented (Expr1004, Bmk1000, a CONVERT_IMPLICIT wrapper) exist only
    /// inside the plan. Matching them against SQL text can only produce false positives.
    /// </summary>
    private static bool IsGeneratedName(string text) =>
        GeneratedName().IsMatch(text) || text.Contains("CONVERT_IMPLICIT", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whole-word containment over already-normalised text. A dot is not a word character, so
    /// <c>OrderDate</c> is found inside <c>o.OrderDate</c>, while <c>Orders</c> is not found
    /// inside <c>OrdersHistory</c>.
    /// </summary>
    private static bool ContainsWord(string haystack, string needle)
    {
        if (needle.Length == 0)
        {
            return false;
        }

        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            var before = index == 0 ? ' ' : haystack[index - 1];
            var afterIndex = index + needle.Length;
            var after = afterIndex >= haystack.Length ? ' ' : haystack[afterIndex];

            if (!IsWordChar(before) && !IsWordChar(after))
            {
                return true;
            }

            index = haystack.IndexOf(needle, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';

    [GeneratedRegex(@"\bas\s+\[([^\]]+)\]\.\[([^\]]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex AliasedColumn();

    [GeneratedRegex(@"\[[^\]@]+\](?:\.\[[^\]]+\])+")]
    private static partial Regex QualifiedName();

    [GeneratedRegex(@"\[(@[A-Za-z0-9_@$#]+)\]")]
    private static partial Regex Parameter();

    [GeneratedRegex(@"'[^']*'")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\((\d{3,})\)")]
    private static partial Regex NumericLiteral();

    [GeneratedRegex(@"^(Expr|Bmk|Uniq|Union|PtnId|Segment|RaiseIfNull)\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex GeneratedName();
}
