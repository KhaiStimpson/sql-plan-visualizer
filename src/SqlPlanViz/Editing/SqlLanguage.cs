namespace SqlPlanViz.Editing;

/// <summary>
/// Static T-SQL vocabulary shared by the tokenizer (to classify bare identifiers that are
/// really type names or built-in functions) and by the keyword completion provider.
///
/// ScriptDom's lexer only marks the tokens that are reserved words; <c>VARCHAR</c>,
/// <c>COUNT</c> and <c>GETDATE</c> all arrive as plain identifiers, and colouring them the
/// same as a table name loses most of the value of highlighting.
/// </summary>
public static class SqlLanguage
{
    /// <summary>Reserved and contextual keywords, for completion. The tokenizer uses ScriptDom's token types instead.</summary>
    public static readonly IReadOnlyList<string> Keywords =
    [
        "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC", "BEGIN", "BETWEEN", "BREAK", "BY",
        "CASE", "CAST", "CATCH", "CHECK", "CLOSE", "CLUSTERED", "COLLATE", "COLUMN", "COMMIT",
        "CONSTRAINT", "CONTINUE", "CREATE", "CROSS", "CURRENT", "CURSOR", "DATABASE", "DECLARE",
        "DEFAULT", "DELETE", "DESC", "DISTINCT", "DROP", "ELSE", "END", "EXCEPT", "EXEC",
        "EXECUTE", "EXISTS", "FETCH", "FOR", "FOREIGN", "FROM", "FULL", "FUNCTION", "GO",
        "GROUP", "HAVING", "IDENTITY", "IF", "IN", "INDEX", "INNER", "INSERT", "INTERSECT",
        "INTO", "IS", "JOIN", "KEY", "LEFT", "LIKE", "MERGE", "NOT", "NULL", "OFFSET", "ON",
        "OPEN", "OPTION", "OR", "ORDER", "OUTER", "OVER", "PARTITION", "PERCENT", "PIVOT",
        "PRIMARY", "PRINT", "PROCEDURE", "RAISERROR", "RETURN", "REVERT", "RIGHT", "ROLLBACK",
        "ROWCOUNT", "SELECT", "SET", "SOME", "TABLE", "THEN", "THROW", "TOP", "TRAN",
        "TRANSACTION", "TRIGGER", "TRUNCATE", "TRY", "UNION", "UNIQUE", "UNPIVOT", "UPDATE",
        "USE", "VALUES", "VIEW", "WHEN", "WHERE", "WHILE", "WITH",
    ];

    /// <summary>Clause snippets offered as single completion items — the shapes people type constantly.</summary>
    public static readonly IReadOnlyList<(string Label, string Insert)> Snippets =
    [
        ("SELECT … FROM", "SELECT \nFROM "),
        ("INNER JOIN … ON", "INNER JOIN  ON "),
        ("LEFT JOIN … ON", "LEFT JOIN  ON "),
        ("GROUP BY", "GROUP BY "),
        ("ORDER BY", "ORDER BY "),
        ("OPTION (RECOMPILE)", "OPTION (RECOMPILE)"),
        ("CROSS APPLY", "CROSS APPLY "),
        ("OUTER APPLY", "OUTER APPLY "),
        ("CASE WHEN … END", "CASE WHEN  THEN  ELSE  END"),
        ("COMMON TABLE EXPRESSION", "WITH cte AS (\n    SELECT \n)\nSELECT * FROM cte"),
    ];

    public static readonly IReadOnlySet<string> DataTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "BIGINT", "BINARY", "BIT", "CHAR", "DATE", "DATETIME", "DATETIME2", "DATETIMEOFFSET",
        "DECIMAL", "FLOAT", "GEOGRAPHY", "GEOMETRY", "HIERARCHYID", "IMAGE", "INT", "MONEY",
        "NCHAR", "NTEXT", "NUMERIC", "NVARCHAR", "REAL", "ROWVERSION", "SMALLDATETIME",
        "SMALLINT", "SMALLMONEY", "SQL_VARIANT", "SYSNAME", "TEXT", "TIME", "TIMESTAMP",
        "TINYINT", "UNIQUEIDENTIFIER", "VARBINARY", "VARCHAR", "XML",
    };

    public static readonly IReadOnlySet<string> Functions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ABS", "AVG", "CEILING", "CHARINDEX", "CHECKSUM", "COALESCE", "CONCAT", "CONCAT_WS",
        "CONVERT", "COUNT", "COUNT_BIG", "CURRENT_TIMESTAMP", "DATEADD", "DATEDIFF",
        "DATEFROMPARTS", "DATENAME", "DATEPART", "DAY", "DENSE_RANK", "EOMONTH", "FLOOR",
        "FORMAT", "GETDATE", "GETUTCDATE", "IIF", "ISDATE", "ISNULL", "ISNUMERIC", "JSON_VALUE",
        "JSON_QUERY", "LAG", "LEAD", "LEN", "LOWER", "LTRIM", "MAX", "MIN", "MONTH", "NEWID",
        "NTILE", "NULLIF", "OBJECT_NAME", "OPENJSON", "PARSE", "PATINDEX", "POWER", "RANK",
        "REPLACE", "REPLICATE", "REVERSE", "ROUND", "ROW_NUMBER", "RTRIM", "SCOPE_IDENTITY",
        "STDEV", "STRING_AGG", "STRING_SPLIT", "STUFF", "SUBSTRING", "SUM", "SWITCHOFFSET",
        "SYSDATETIME", "SYSUTCDATETIME", "TRIM", "TRY_CAST", "TRY_CONVERT", "TRY_PARSE",
        "UPPER", "VAR", "YEAR",
    };

    /// <summary>True for a name that is really a type — used to colour it and to rank it in completion.</summary>
    public static bool IsDataType(string text) => DataTypes.Contains(text);

    public static bool IsFunction(string text) => Functions.Contains(text);
}
