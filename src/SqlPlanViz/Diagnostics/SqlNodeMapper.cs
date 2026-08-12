using System.Text.RegularExpressions;
using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics;

public sealed record SqlTextSpan(int Start, int Length, string Clause);

/// <summary>
/// Best-effort operator-to-SQL mapping. Showplan does not carry source spans, so this uses
/// predicates, object names, and operator semantics to highlight the most likely clause.
/// </summary>
public static partial class SqlNodeMapper
{
    public static SqlTextSpan? Map(string sql, PlanNode node)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return null;
        }

        var preferredClause = PreferredClause(node);
        var clauseStart = IndexOf(sql, preferredClause);

        var expression = node.SeekPredicate ?? node.Predicate;
        if (!string.IsNullOrWhiteSpace(expression))
        {
            foreach (Match match in BracketedIdentifier().Matches(expression).Cast<Match>().Reverse())
            {
                var identifier = match.Groups[1].Value;
                var mention = FindIdentifier(sql, identifier, Math.Max(0, clauseStart));
                if (mention >= 0)
                {
                    return LineContaining(sql, mention, preferredClause);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(node.ObjectName))
        {
            var objectPart = node.ObjectName.Split('.').Last().Trim('[', ']');
            var mention = FindIdentifier(sql, objectPart, Math.Max(0, clauseStart));
            if (mention >= 0)
            {
                return LineContaining(sql, mention, preferredClause);
            }
        }

        if (clauseStart >= 0)
        {
            return ClauseSpan(sql, clauseStart, preferredClause);
        }

        return LineContaining(sql, 0, "Statement");
    }

    private static string PreferredClause(PlanNode node)
    {
        var op = $"{node.PhysicalOp} {node.LogicalOp}";
        if (op.Contains("Sort", StringComparison.OrdinalIgnoreCase)) return "ORDER BY";
        if (op.Contains("Aggregate", StringComparison.OrdinalIgnoreCase)) return "GROUP BY";
        if (op.Contains("Join", StringComparison.OrdinalIgnoreCase) || op.Contains("Nested Loops", StringComparison.OrdinalIgnoreCase)) return "JOIN";
        if (op.Contains("Insert", StringComparison.OrdinalIgnoreCase)) return "INSERT";
        if (op.Contains("Update", StringComparison.OrdinalIgnoreCase)) return "UPDATE";
        if (op.Contains("Delete", StringComparison.OrdinalIgnoreCase)) return "DELETE";
        if (node.Predicate is not null || node.SeekPredicate is not null || op.Contains("Filter", StringComparison.OrdinalIgnoreCase)) return "WHERE";
        if (op.Contains("Scan", StringComparison.OrdinalIgnoreCase) || op.Contains("Seek", StringComparison.OrdinalIgnoreCase)) return "FROM";
        return "SELECT";
    }

    private static int FindIdentifier(string sql, string identifier, int start)
    {
        var bracketed = sql.IndexOf($"[{identifier}]", start, StringComparison.OrdinalIgnoreCase);
        return bracketed >= 0 ? bracketed : sql.IndexOf(identifier, start, StringComparison.OrdinalIgnoreCase);
    }

    private static int IndexOf(string sql, string clause) => sql.IndexOf(clause, StringComparison.OrdinalIgnoreCase);

    private static SqlTextSpan LineContaining(string sql, int position, string clause)
    {
        var start = position <= 0 ? 0 : sql.LastIndexOf('\n', Math.Min(position, sql.Length - 1)) + 1;
        var end = sql.IndexOf('\n', Math.Min(position, sql.Length));
        if (end < 0) end = sql.Length;
        while (start < end && char.IsWhiteSpace(sql[start]) && sql[start] != '\n') start++;
        return new SqlTextSpan(start, Math.Max(1, end - start), clause);
    }

    private static SqlTextSpan ClauseSpan(string sql, int start, string clause)
    {
        var terminators = new[] { " WHERE ", " GROUP BY ", " ORDER BY ", " HAVING ", ";" };
        var end = sql.Length;
        foreach (var terminator in terminators)
        {
            var candidate = sql.IndexOf(terminator, start + clause.Length, StringComparison.OrdinalIgnoreCase);
            if (candidate >= 0) end = Math.Min(end, candidate);
        }

        return new SqlTextSpan(start, Math.Max(1, end - start), clause);
    }

    [GeneratedRegex(@"\[([^\]]+)\]")]
    private static partial Regex BracketedIdentifier();
}
