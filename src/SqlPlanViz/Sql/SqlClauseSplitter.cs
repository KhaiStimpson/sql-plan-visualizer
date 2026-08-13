using System.Text;

namespace SqlPlanViz.Sql;

/// <summary>
/// One clause of a statement: the keyword and everything up to the next clause, including its
/// continuation lines (a JOIN keeps its ON, a WHERE keeps its ANDs).
/// </summary>
public sealed record SqlClauseRegion(string Kind, int Start, int Length, string SearchText)
{
    public int End => Start + Length;
}

/// <summary>
/// Splits a statement into clause regions. This runs over tokens rather than lines, so it gives
/// the same answer whether the statement is formatted one clause per line or arrived as a single
/// 400-character line — which matters, because that is how plans usually carry their SQL.
/// </summary>
public static class SqlClauseSplitter
{
    /// <summary>Used when a statement has no recognisable clause keyword at all.</summary>
    public const string StatementKind = "statement";

    public static IReadOnlyList<SqlClauseRegion> Split(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return [];
        }

        var tokens = SqlTokenizer.Tokenize(sql);
        var starts = new List<(int Offset, string Kind)>();
        var depth = 0;
        var previousUpper = string.Empty;

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Kind is SqlTokenKind.Whitespace or SqlTokenKind.Comment)
            {
                continue;
            }

            var upper = token.Text(sql).ToUpperInvariant();

            if (upper == "(")
            {
                depth++;
            }
            else if (upper == ")")
            {
                depth = Math.Max(0, depth - 1);
            }

            // A subquery's own SELECT/WHERE belong to the clause that contains them.
            else if (depth == 0 && token.Kind == SqlTokenKind.Keyword
                     && ClauseKind(tokens, index, sql, upper, previousUpper) is { } kind)
            {
                starts.Add((token.Start, kind));
            }

            previousUpper = upper;
        }

        if (starts.Count == 0)
        {
            return [new SqlClauseRegion(StatementKind, 0, sql.Length, Normalize(sql))];
        }

        var regions = new List<SqlClauseRegion>(starts.Count);
        for (var i = 0; i < starts.Count; i++)
        {
            var start = starts[i].Offset;
            var end = i + 1 < starts.Count ? starts[i + 1].Offset : sql.Length;
            while (end > start && (char.IsWhiteSpace(sql[end - 1]) || sql[end - 1] == ';'))
            {
                end--;
            }

            if (end > start)
            {
                regions.Add(new SqlClauseRegion(starts[i].Kind, start, end - start, Normalize(sql[start..end])));
            }
        }

        return regions;
    }

    /// <summary>
    /// Upper-cased with brackets and quotes dropped, so <c>[o].[OrderDate]</c> and
    /// <c>o.OrderDate</c> — the two ways the same column reaches us — compare equal.
    /// </summary>
    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is not ('[' or ']' or '"'))
            {
                sb.Append(char.ToUpperInvariant(c));
            }
        }

        return sb.ToString();
    }

    private static string? ClauseKind(
        IReadOnlyList<SqlToken> tokens,
        int index,
        string sql,
        string upper,
        string previousUpper)
    {
        if (upper is "JOIN" or "APPLY")
        {
            return JoinLeadWords.Contains(previousUpper) ? null : "JOIN";
        }

        if (JoinLeadWords.Contains(upper))
        {
            return !JoinLeadWords.Contains(previousUpper) && LeadsToJoin(tokens, index, sql) ? "JOIN" : null;
        }

        return upper switch
        {
            "GROUP" => NextSignificantUpper(tokens, index, sql) == "BY" ? "GROUP BY" : null,
            "ORDER" => NextSignificantUpper(tokens, index, sql) == "BY" ? "ORDER BY" : null,
            "UNION" or "EXCEPT" or "INTERSECT" => "UNION",
            "SELECT" or "FROM" or "WHERE" or "HAVING" or "INSERT" or "UPDATE" or "DELETE"
                or "MERGE" or "VALUES" or "SET" or "OUTPUT" or "OPTION" => upper,
            _ => null,
        };
    }

    private static bool LeadsToJoin(IReadOnlyList<SqlToken> tokens, int index, string sql)
    {
        var probe = index;
        for (var step = 0; step < 2; step++)
        {
            probe = SqlTokenizer.NextSignificant(tokens, probe + 1);
            if (probe < 0)
            {
                return false;
            }

            var next = tokens[probe].Text(sql).ToUpperInvariant();
            if (next is "JOIN" or "APPLY")
            {
                return true;
            }

            if (!JoinLeadWords.Contains(next))
            {
                return false;
            }
        }

        return false;
    }

    private static string NextSignificantUpper(IReadOnlyList<SqlToken> tokens, int index, string sql)
    {
        var next = SqlTokenizer.NextSignificant(tokens, index + 1);
        return next < 0 ? string.Empty : tokens[next].Text(sql).ToUpperInvariant();
    }

    private static readonly HashSet<string> JoinLeadWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER",
    };
}
