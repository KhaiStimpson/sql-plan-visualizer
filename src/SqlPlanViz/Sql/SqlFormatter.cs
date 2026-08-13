using System.Text;

namespace SqlPlanViz.Sql;

/// <summary>
/// Re-lays out a statement so its clauses are readable. Plans routinely carry their SQL as one
/// 400-character line (that is how the app that ran it sent it), which makes "highlight the
/// clause this operator came from" useless — the whole query is one line. Rebuilding the
/// whitespace gives every clause a line of its own, which is what the operator→SQL mapping
/// highlights and scrolls to.
///
/// This is a formatter, not a parser: token text is never rewritten (no keyword re-casing, no
/// reordering), only the whitespace between tokens is. Anything it does not understand falls
/// through as a plain token, so unusual syntax degrades to "one long line", never to wrong SQL.
/// </summary>
public static class SqlFormatter
{
    private const string IndentUnit = "    ";

    /// <summary>Past this, formatting is not worth the pause — huge batches are pasted, not read.</summary>
    private const int MaxFormattedLength = 200_000;

    private enum ParenKind
    {
        /// <summary>A function call, an expression group, a column or value list — stays on one line.</summary>
        Inline,

        /// <summary>Wraps a SELECT (derived table, CTE body, scalar subquery) — gets its own block.</summary>
        Subquery,
    }

    private enum BlockKind
    {
        Case,
        Begin,
    }

    /// <summary>An open CASE or BEGIN, and the indent level the line it started on was written at.</summary>
    private readonly record struct Block(BlockKind Kind, int Level);

    /// <summary>An open paren: what to restore the indent to, and where its closing paren lines up.</summary>
    private readonly record struct Paren(ParenKind Kind, int SavedIndent, int OpenLevel);

    /// <summary>Line endings only, for text that should keep the author's layout.</summary>
    public static string Normalize(string sql) => string.IsNullOrEmpty(sql)
        ? string.Empty
        : sql.Replace("\r\n", "\n").Replace('\r', '\n');

    public static string Format(string sql)
    {
        var text = Normalize(sql).Trim();
        if (text.Length == 0 || text.Length > MaxFormattedLength)
        {
            return text;
        }

        var tokens = SqlTokenizer.Tokenize(text);
        var writer = new LineWriter();
        var parens = new Stack<Paren>();
        var blocks = new Stack<Block>();
        var indent = 0;

        // A subquery or a CASE nests under wherever it started, which is often a continuation
        // line rather than the statement level, so both indent from the line they opened on.
        var currentLevel = 0;
        int? carriedBreak = null;
        var hugNext = false;
        var pendingBetween = false;
        var previousKind = (SqlTokenKind?)null;
        var previousUpper = string.Empty;
        var seenFirstToken = false;

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Kind == SqlTokenKind.Whitespace)
            {
                continue;
            }

            var value = token.Text(text);
            var upper = value.ToUpperInvariant();

            // Breaking is only safe at the outer level of a statement: inside a function call or
            // a value list, a newline before every comma reads far worse than the long line does.
            var breakable = parens.Count == 0 || parens.Peek().Kind == ParenKind.Subquery;
            int? breakLevel = null;
            var pushBlock = (BlockKind?)null;

            if (value == ")")
            {
                if (parens.Count > 0)
                {
                    var paren = parens.Pop();
                    if (paren.Kind == ParenKind.Subquery)
                    {
                        indent = paren.SavedIndent;
                        breakLevel = paren.OpenLevel;
                    }
                }
            }
            else if (token.Kind == SqlTokenKind.Keyword)
            {
                var inCase = blocks.Count > 0 && blocks.Peek().Kind == BlockKind.Case;

                if (upper == "CASE")
                {
                    pushBlock = BlockKind.Case;
                }
                else if (upper == "BEGIN" && !OpensTransaction(tokens, index, text))
                {
                    pushBlock = BlockKind.Begin;
                    if (breakable)
                    {
                        breakLevel = indent;
                    }
                }
                else if (upper == "END" && blocks.Count > 0)
                {
                    var closed = blocks.Pop();
                    if (closed.Kind == BlockKind.Begin)
                    {
                        indent = closed.Level;
                    }

                    if (breakable)
                    {
                        breakLevel = closed.Level;
                    }
                }
                else if (breakable && inCase && (upper is "WHEN" or "ELSE"))
                {
                    breakLevel = blocks.Peek().Level + 1;
                }
                else if (breakable && upper == "WHEN")
                {
                    // Outside a CASE, WHEN is a MERGE action and belongs at statement level.
                    breakLevel = indent;
                }
                else if (breakable && (upper is "ON" or "AND" or "OR"))
                {
                    // BETWEEN x AND y is one predicate; breaking at its AND splits it in half.
                    if (!(upper == "AND" && pendingBetween))
                    {
                        breakLevel = indent + 1;
                    }
                }
                else if (breakable
                         && (StartsJoin(tokens, index, text, upper, previousUpper)
                             || StartsClause(tokens, index, text, upper, previousUpper, seenFirstToken)))
                {
                    breakLevel = indent;
                }
            }

            if (breakLevel is null && carriedBreak is int carried)
            {
                breakLevel = carried;
            }

            carriedBreak = null;

            if (breakLevel is int level)
            {
                writer.StartLine(level);
                currentLevel = level;
                hugNext = false;
            }

            writer.Append(value, spaceBefore: !hugNext && !HugsPrevious(value, previousKind));
            hugNext = false;

            // Pushed after the line break, so a block remembers the line it actually landed on.
            if (pushBlock is BlockKind blockKind)
            {
                blocks.Push(new Block(blockKind, currentLevel));
                if (blockKind == BlockKind.Begin)
                {
                    indent = currentLevel + 1;
                }
            }

            switch (value)
            {
                case "(":
                    var subquery = breakable && OpensSubquery(tokens, index, text);
                    parens.Push(new Paren(
                        subquery ? ParenKind.Subquery : ParenKind.Inline,
                        SavedIndent: indent,
                        OpenLevel: currentLevel));

                    if (subquery)
                    {
                        indent = currentLevel + 1;
                    }
                    else
                    {
                        hugNext = true;
                    }

                    break;

                case ".":
                    hugNext = true;
                    break;

                case "," when breakable:
                    carriedBreak = indent + 1;
                    break;

                case ";":
                    // The next statement resumes at the level of the block it sits in.
                    indent = blocks.Count > 0 && blocks.Peek().Kind == BlockKind.Begin
                        ? blocks.Peek().Level + 1
                        : 0;
                    carriedBreak = indent;
                    break;
            }

            pendingBetween = upper switch
            {
                "BETWEEN" => true,
                "AND" => false,
                _ => pendingBetween,
            };

            // A line comment runs to the end of its line, so whatever follows must start a new one.
            if (token.Kind == SqlTokenKind.Comment && value.StartsWith("--", StringComparison.Ordinal))
            {
                carriedBreak = indent;
            }

            previousKind = token.Kind;
            previousUpper = upper;
            seenFirstToken = true;
        }

        return writer.ToString();
    }

    /// <summary>Clause keywords that earn a line of their own at the current statement level.</summary>
    private static bool StartsClause(
        IReadOnlyList<SqlToken> tokens,
        int index,
        string text,
        string upper,
        string previousUpper,
        bool seenFirstToken)
    {
        switch (upper)
        {
            // GROUP/ORDER are only clauses in front of BY; alone they are almost certainly a column.
            case "GROUP" or "ORDER":
                return NextSignificantUpper(tokens, index, text) == "BY";

            // WITH opens a CTE at the head of a statement and a table hint everywhere else.
            case "WITH":
                return !seenFirstToken || previousUpper == ";";

            case "FROM":
                return previousUpper != "DELETE";

            case "IF":
                return previousUpper != "ELSE";

            default:
                return ClauseKeywords.Contains(upper);
        }
    }

    private static bool StartsJoin(
        IReadOnlyList<SqlToken> tokens,
        int index,
        string text,
        string upper,
        string previousUpper)
    {
        if (upper is "JOIN" or "APPLY")
        {
            // INNER / LEFT OUTER / CROSS already started the line.
            return !JoinLeadWords.Contains(previousUpper);
        }

        if (!JoinLeadWords.Contains(upper) || JoinLeadWords.Contains(previousUpper))
        {
            return false;
        }

        // LEFT and RIGHT are also functions and CROSS shows up in CROSS APPLY: only treat the
        // word as a join when a JOIN or APPLY actually follows it.
        var probe = index;
        for (var step = 0; step < 2; step++)
        {
            probe = SqlTokenizer.NextSignificant(tokens, probe + 1);
            if (probe < 0)
            {
                return false;
            }

            var next = tokens[probe].Text(text).ToUpperInvariant();
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

    private static bool OpensSubquery(IReadOnlyList<SqlToken> tokens, int index, string text) =>
        NextSignificantUpper(tokens, index, text) is "SELECT" or "WITH";

    private static bool OpensTransaction(IReadOnlyList<SqlToken> tokens, int index, string text) =>
        NextSignificantUpper(tokens, index, text) is "TRAN" or "TRANSACTION" or "DISTRIBUTED";

    private static string NextSignificantUpper(IReadOnlyList<SqlToken> tokens, int index, string text)
    {
        var next = SqlTokenizer.NextSignificant(tokens, index + 1);
        return next < 0 ? string.Empty : tokens[next].Text(text).ToUpperInvariant();
    }

    private static bool HugsPrevious(string value, SqlTokenKind? previousKind) =>
        value is "," or ";" or ")" or "." or "::"
        || (value == "(" && previousKind == SqlTokenKind.Function);

    private static readonly HashSet<string> ClauseKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "HAVING", "UNION", "EXCEPT", "INTERSECT", "INSERT", "UPDATE",
        "DELETE", "MERGE", "VALUES", "SET", "OPTION", "OUTPUT", "DECLARE", "EXEC", "EXECUTE",
        "RETURN", "PRINT", "TRUNCATE", "CREATE", "ALTER", "DROP", "WHILE", "FETCH", "OPEN",
        "CLOSE", "WAITFOR", "USE", "GRANT", "REVOKE", "DENY", "RAISERROR", "COMMIT", "ROLLBACK",
        "SAVE", "BREAK", "CONTINUE", "GOTO", "PIVOT", "UNPIVOT", "BEGIN", "END", "ELSE",
    };

    private static readonly HashSet<string> JoinLeadWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER",
    };

    /// <summary>Builds the output a token at a time, owning every space and newline in it.</summary>
    private sealed class LineWriter
    {
        private readonly StringBuilder _builder = new();
        private bool _lineHasContent;

        public void StartLine(int level)
        {
            if (_builder.Length > 0)
            {
                _builder.Append('\n');
            }

            for (var i = 0; i < level; i++)
            {
                _builder.Append(IndentUnit);
            }

            _lineHasContent = false;
        }

        public void Append(string value, bool spaceBefore)
        {
            if (spaceBefore && _lineHasContent)
            {
                _builder.Append(' ');
            }

            _builder.Append(value);
            _lineHasContent = true;
        }

        public override string ToString() => _builder.ToString();
    }
}
