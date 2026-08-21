using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlPlanViz.Editing;

/// <summary>The T-SQL dialects ScriptDom 180.x can parse, newest last.</summary>
public enum SqlParserVersion
{
    Sql2012,
    Sql2014,
    Sql2016,
    Sql2017,
    Sql2019,
    Sql2022,
}

/// <summary>
/// Builds the ScriptDom parser the rest of <c>Editing/</c> uses.
///
/// live-plan-editor-plan.md calls out the version mismatch risk: a fixed parser rejects
/// syntax newer than its target and accepts syntax an older server will not. So the version
/// is derived from <c>@@VERSION</c> when a connection exists and falls back to the newest
/// otherwise, and every caller treats parse failures as non-fatal.
/// </summary>
public static class TSqlParserFactory
{
    public static SqlParserVersion Default { get; set; } = SqlParserVersion.Sql2022;

    public static TSqlParser Create(SqlParserVersion? version = null, bool quotedIdentifiers = true) =>
        (version ?? Default) switch
        {
            SqlParserVersion.Sql2012 => new TSql110Parser(quotedIdentifiers),
            SqlParserVersion.Sql2014 => new TSql120Parser(quotedIdentifiers),
            SqlParserVersion.Sql2016 => new TSql130Parser(quotedIdentifiers),
            SqlParserVersion.Sql2017 => new TSql140Parser(quotedIdentifiers),
            SqlParserVersion.Sql2019 => new TSql150Parser(quotedIdentifiers),
            _ => new TSql160Parser(quotedIdentifiers),
        };

    /// <summary>
    /// Maps the first line of <c>@@VERSION</c> — "Microsoft SQL Server 2019 (RTM) …" — onto a
    /// parser. Azure SQL Database reports no year, so it gets the newest parser, which is what
    /// it actually is. Anything unrecognised falls back to <see cref="Default"/>.
    /// </summary>
    public static SqlParserVersion FromServerVersion(string? versionBanner)
    {
        if (string.IsNullOrWhiteSpace(versionBanner))
        {
            return Default;
        }

        if (versionBanner.Contains("Azure", StringComparison.OrdinalIgnoreCase))
        {
            return SqlParserVersion.Sql2022;
        }

        if (versionBanner.Contains(" 2022", StringComparison.Ordinal)) return SqlParserVersion.Sql2022;
        if (versionBanner.Contains(" 2019", StringComparison.Ordinal)) return SqlParserVersion.Sql2019;
        if (versionBanner.Contains(" 2017", StringComparison.Ordinal)) return SqlParserVersion.Sql2017;
        if (versionBanner.Contains(" 2016", StringComparison.Ordinal)) return SqlParserVersion.Sql2016;
        if (versionBanner.Contains(" 2014", StringComparison.Ordinal)) return SqlParserVersion.Sql2014;
        if (versionBanner.Contains(" 2012", StringComparison.Ordinal)) return SqlParserVersion.Sql2012;

        return Default;
    }

    /// <summary>Parses a batch, returning null when it does not parse. Never throws for bad SQL.</summary>
    public static TSqlFragment? TryParse(string sql, out IList<ParseError> errors, SqlParserVersion? version = null)
    {
        errors = [];
        if (string.IsNullOrWhiteSpace(sql))
        {
            return null;
        }

        try
        {
            using var reader = new StringReader(sql);
            var fragment = Create(version).Parse(reader, out errors);
            return errors is { Count: > 0 } && fragment is null ? null : fragment;
        }
        catch (Exception)
        {
            // ScriptDom is robust, but a parse failure must never take the editor down with it.
            errors = [];
            return null;
        }
    }
}
