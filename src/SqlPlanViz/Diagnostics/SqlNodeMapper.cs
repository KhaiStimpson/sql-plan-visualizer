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
        start = ExtendBackToClause(sql, start);
        return new SqlTextSpan(start, Math.Max(1, ExtendThroughContinuations(sql, end) - start), clause);
    }

    /// <summary>
    /// Landing on an <c>ON</c> or <c>AND</c> line means the operator matched the predicate, not
    /// the clause that owns it — walk up so the highlight starts at the JOIN or WHERE itself.
    /// </summary>
    private static int ExtendBackToClause(string sql, int start)
    {
        for (var guard = 0; guard < 8 && start > 0 && StartsContinuation(sql, start); guard++)
        {
            var lineStart = sql.LastIndexOf('\n', start - 1) + 1;
            if (lineStart == 0)
            {
                break;
            }

            var previousStart = lineStart >= 2 ? sql.LastIndexOf('\n', lineStart - 2) + 1 : 0;
            var text = previousStart;
            while (text < lineStart && sql[text] is ' ' or '\t') text++;

            // A blank line above means this predicate has no clause line to fold into.
            if (text >= lineStart - 1)
            {
                break;
            }

            start = text;
        }

        return start;
    }

    /// <summary>
    /// A join or a filter is rarely one line: <c>ON</c>, <c>AND</c> and <c>OR</c> continue it on
    /// the following indented lines. Highlighting only the first line would cut the predicate in
    /// half, which is exactly the part worth reading.
    /// </summary>
    private static int ExtendThroughContinuations(string sql, int end)
    {
        while (end < sql.Length && sql[end] == '\n')
        {
            var lineStart = end + 1;
            var text = lineStart;
            while (text < sql.Length && sql[text] is ' ' or '\t') text++;

            // An unindented line is the next clause, not a continuation of this one.
            if (text == lineStart || !StartsContinuation(sql, text))
            {
                break;
            }

            var lineEnd = sql.IndexOf('\n', text);
            end = lineEnd < 0 ? sql.Length : lineEnd;
        }

        return end;
    }

    private static bool StartsContinuation(string sql, int position)
    {
        foreach (var word in Continuations)
        {
            if (position + word.Length > sql.Length
                || !sql.AsSpan(position, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var after = position + word.Length;
            if (after >= sql.Length || char.IsWhiteSpace(sql[after]) || sql[after] == '(')
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] Continuations = ["ON", "AND", "OR"];

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
