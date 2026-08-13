namespace SqlPlanViz.Sql;

/// <summary>What a <see cref="SqlToken"/> is, for colouring and for formatting decisions.</summary>
public enum SqlTokenKind
{
    Whitespace,
    Comment,
    String,
    Number,
    Keyword,
    Function,
    Identifier,
    Variable,
    Operator,
    Punctuation,
}

/// <summary>A half-open slice of the SQL text. Offsets stay in the caller's coordinates so a
/// token can be mapped straight back onto the string that produced it.</summary>
public readonly record struct SqlToken(SqlTokenKind Kind, int Start, int Length)
{
    public int End => Start + Length;

    public string Text(string sql) => sql.Substring(Start, Length);
}

/// <summary>
/// A small, forgiving T-SQL lexer. It is not a parser: it never fails, never rejects input, and
/// covers exactly what a syntax-highlighted read-only view needs — comments, literals, quoted
/// identifiers, variables, keywords, and everything else lumped into identifiers/operators.
/// Statement text pulled out of a plan is often truncated mid-token, so every scanner treats
/// end-of-input as a valid terminator.
/// </summary>
public static class SqlTokenizer
{
    public static IReadOnlyList<SqlToken> Tokenize(string sql)
    {
        var tokens = new List<SqlToken>();
        if (string.IsNullOrEmpty(sql))
        {
            return tokens;
        }

        var i = 0;
        while (i < sql.Length)
        {
            var start = i;
            var c = sql[i];

            void Add(SqlTokenKind kind) => tokens.Add(new SqlToken(kind, start, i - start));

            if (char.IsWhiteSpace(c))
            {
                while (i < sql.Length && char.IsWhiteSpace(sql[i])) i++;
                Add(SqlTokenKind.Whitespace);
                continue;
            }

            if (c == '-' && Peek(sql, i + 1) == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                Add(SqlTokenKind.Comment);
                continue;
            }

            if (c == '/' && Peek(sql, i + 1) == '*')
            {
                i += 2;

                // T-SQL block comments nest, so a naive scan to the first */ would end early.
                var depth = 1;
                while (i < sql.Length && depth > 0)
                {
                    if (sql[i] == '/' && Peek(sql, i + 1) == '*')
                    {
                        depth++;
                        i += 2;
                    }
                    else if (sql[i] == '*' && Peek(sql, i + 1) == '/')
                    {
                        depth--;
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }
                }

                Add(SqlTokenKind.Comment);
                continue;
            }

            if (c == '\'' || ((c is 'N' or 'n') && Peek(sql, i + 1) == '\''))
            {
                if (c != '\'') i++;
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] != '\'')
                    {
                        i++;
                        continue;
                    }

                    // '' is an escaped quote, not the end of the literal.
                    if (Peek(sql, i + 1) == '\'')
                    {
                        i += 2;
                        continue;
                    }

                    i++;
                    break;
                }

                Add(SqlTokenKind.String);
                continue;
            }

            if (c == '"')
            {
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] != '"')
                    {
                        i++;
                        continue;
                    }

                    if (Peek(sql, i + 1) == '"')
                    {
                        i += 2;
                        continue;
                    }

                    i++;
                    break;
                }

                Add(SqlTokenKind.Identifier);
                continue;
            }

            if (c == '[')
            {
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] != ']')
                    {
                        i++;
                        continue;
                    }

                    if (Peek(sql, i + 1) == ']')
                    {
                        i += 2;
                        continue;
                    }

                    i++;
                    break;
                }

                Add(SqlTokenKind.Identifier);
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && char.IsDigit(Peek(sql, i + 1))))
            {
                if (c == '0' && Peek(sql, i + 1) is 'x' or 'X')
                {
                    i += 2;
                    while (i < sql.Length && Uri.IsHexDigit(sql[i])) i++;
                }
                else
                {
                    while (i < sql.Length && char.IsDigit(sql[i])) i++;
                    if (i < sql.Length && sql[i] == '.')
                    {
                        i++;
                        while (i < sql.Length && char.IsDigit(sql[i])) i++;
                    }

                    if (i < sql.Length && sql[i] is 'e' or 'E')
                    {
                        var beforeExponent = i;
                        i++;
                        if (i < sql.Length && sql[i] is '+' or '-') i++;
                        if (i < sql.Length && char.IsDigit(sql[i]))
                        {
                            while (i < sql.Length && char.IsDigit(sql[i])) i++;
                        }
                        else
                        {
                            i = beforeExponent;
                        }
                    }
                }

                Add(SqlTokenKind.Number);
                continue;
            }

            if (c is '@' or '#')
            {
                while (i < sql.Length && sql[i] is '@' or '#') i++;
                while (i < sql.Length && IsWordChar(sql[i])) i++;
                Add(SqlTokenKind.Variable);
                continue;
            }

            if (IsWordStart(c))
            {
                while (i < sql.Length && IsWordChar(sql[i])) i++;
                var word = sql[start..i];
                Add(Classify(word, sql, i));
                continue;
            }

            if (c is '(' or ')' or ',' or ';' or '.')
            {
                i++;
                Add(SqlTokenKind.Punctuation);
                continue;
            }

            if (IsOperatorChar(c))
            {
                while (i < sql.Length && IsOperatorChar(sql[i])) i++;
                Add(SqlTokenKind.Operator);
                continue;
            }

            i++;
            Add(SqlTokenKind.Identifier);
        }

        return tokens;
    }

    /// <summary>The next token that carries meaning — whitespace and comments skipped.</summary>
    public static int NextSignificant(IReadOnlyList<SqlToken> tokens, int index)
    {
        for (var i = index; i < tokens.Count; i++)
        {
            if (tokens[i].Kind is not (SqlTokenKind.Whitespace or SqlTokenKind.Comment))
            {
                return i;
            }
        }

        return -1;
    }

    private static SqlTokenKind Classify(string word, string sql, int wordEnd)
    {
        if (BuiltInFunctions.Contains(word))
        {
            return SqlTokenKind.Function;
        }

        if (Keywords.Contains(word))
        {
            // LEFT and RIGHT are join words and string functions; only the call has a paren.
            return AlsoFunctions.Contains(word) && FollowedByOpenParen(sql, wordEnd)
                ? SqlTokenKind.Function
                : SqlTokenKind.Keyword;
        }

        // Anything called like a function reads like one, which catches user-defined functions
        // without carrying a dictionary of them.
        return FollowedByOpenParen(sql, wordEnd) ? SqlTokenKind.Function : SqlTokenKind.Identifier;
    }

    private static bool FollowedByOpenParen(string sql, int index)
    {
        while (index < sql.Length && char.IsWhiteSpace(sql[index])) index++;
        return index < sql.Length && sql[index] == '(';
    }

    private static char Peek(string sql, int index) => index < sql.Length ? sql[index] : '\0';

    private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#' or '@';

    private static bool IsOperatorChar(char c) => c is '+' or '-' or '*' or '/' or '%' or '='
        or '<' or '>' or '!' or '~' or '^' or '&' or '|' or ':';

    /// <summary>
    /// Reserved words only. Words that are frequently column names (name, value, key, state,
    /// type, date, level, status) are deliberately absent — colouring a column as a keyword is
    /// worse than leaving a rare keyword plain.
    /// </summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADD", "ALL", "ALTER", "AND", "ANY", "APPLY", "AS", "ASC", "AUTHORIZATION", "BACKUP",
        "BEGIN", "BETWEEN", "BREAK", "BROWSE", "BULK", "BY", "CASCADE", "CASE", "CATCH", "CHECK",
        "CHECKPOINT", "CLOSE", "CLUSTERED", "COALESCE", "COLLATE", "COLUMN", "COMMIT", "COMPUTE",
        "CONSTRAINT", "CONTAINS", "CONTINUE", "CONVERT", "CREATE", "CROSS", "CURRENT", "CURSOR",
        "DATABASE", "DBCC", "DEALLOCATE", "DECLARE", "DEFAULT", "DELETE", "DENY", "DESC",
        "DISABLE", "DISTINCT", "DISTRIBUTED", "DROP", "ELSE", "ENABLE", "END", "ERRLVL",
        "ESCAPE", "EXCEPT", "EXEC", "EXECUTE", "EXISTS", "EXIT", "EXTERNAL", "FETCH", "FILE",
        "FILLFACTOR", "FOR", "FOREIGN", "FREETEXT", "FROM", "FULL", "FUNCTION", "GOTO", "GRANT",
        "GROUP", "HAVING", "HOLDLOCK", "IDENTITY", "IF", "IN", "INDEX", "INNER", "INSERT",
        "INTERSECT", "INTO", "IS", "JOIN", "KEY", "KILL", "LEFT", "LIKE", "LINENO", "MERGE",
        "NATIONAL", "NOCHECK", "NOLOCK", "NONCLUSTERED", "NOT", "NULL", "NULLIF", "OF", "OFF",
        "OFFSETS", "ON", "OPEN", "OPTION", "OR", "ORDER", "OUTER", "OUTPUT", "OVER", "PARTITION",
        "PERCENT", "PIVOT", "PLAN", "PRECISION", "PRIMARY", "PRINT", "PROC", "PROCEDURE",
        "PUBLIC", "RAISERROR", "READ", "READTEXT", "RECOMPILE", "RECONFIGURE", "REFERENCES",
        "REPLICATION", "RESTORE", "RESTRICT", "RETURN", "REVERT", "REVOKE", "RIGHT", "ROLLBACK",
        "ROW", "ROWCOUNT", "ROWGUIDCOL", "ROWS", "RULE", "SAVE", "SCHEMA", "SECURITYAUDIT",
        "SELECT", "SEMANTICKEYPHRASETABLE", "SESSION_USER", "SET", "SETUSER", "SHUTDOWN", "SOME",
        "STATISTICS", "SYSTEM_USER", "TABLE", "TABLESAMPLE", "TEXTSIZE", "THEN", "TO", "TOP",
        "TRAN", "TRANSACTION", "TRIGGER", "TRUNCATE", "TRY", "TSEQUAL", "UNION", "UNIQUE",
        "UNPIVOT", "UPDATE", "UPDATETEXT", "USE", "USER", "VALUES", "VARYING", "VIEW", "WAITFOR",
        "WHEN", "WHERE", "WHILE", "WITH", "WITHIN", "WRITETEXT",
    };

    /// <summary>Reserved words that are function names when they are called like one.</summary>
    private static readonly HashSet<string> AlsoFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "LEFT", "RIGHT", "CURRENT", "USER", "IDENTITY",
    };

    private static readonly HashSet<string> BuiltInFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ABS", "AVG", "CAST", "CEILING", "CHARINDEX", "CHECKSUM", "COUNT", "COUNT_BIG",
        "CUME_DIST", "CURRENT_TIMESTAMP", "DATEADD", "DATEDIFF", "DATENAME", "DATEPART", "DAY",
        "DENSE_RANK", "EOMONTH", "FLOOR", "FORMAT", "GETDATE", "GETUTCDATE", "GROUPING", "IIF",
        "ISDATE", "ISNULL", "ISNUMERIC", "JSON_VALUE", "LAG", "LEAD", "LEN", "LOWER", "LTRIM",
        "MAX", "MIN", "MONTH", "NEWID", "NTILE", "PARSE", "PATINDEX", "RAND", "RANK", "REPLACE",
        "REPLICATE", "REVERSE", "ROUND", "ROW_NUMBER", "RTRIM", "SCOPE_IDENTITY", "STDEV",
        "STRING_AGG", "STRING_SPLIT", "STUFF", "SUBSTRING", "SUM", "SYSDATETIME",
        "TRIM", "TRY_CAST", "TRY_CONVERT", "TRY_PARSE", "UPPER", "VAR", "YEAR",
    };
}
