namespace SqlPlanViz.Editing.Completion;

/// <summary>
/// The provider that always works: T-SQL keywords, built-in functions, type names and clause
/// snippets, with ranking driven by the clause the caret is in.
///
/// The clause weighting is most of the value. In a WHERE clause, AND, OR, BETWEEN and EXISTS
/// are what you are about to type; SELECT is not, even though it matches "se" perfectly well.
/// </summary>
public sealed class KeywordProvider : ICompletionProvider
{
    public const string ProviderId = "keywords";

    public string Id => ProviderId;

    public string DisplayName => "Keywords and built-in functions";

    public bool IsEnabled { get; set; } = true;

    public IEnumerable<CompletionItem> GetItems(CompletionContext context)
    {
        // After a dot, a keyword is never the answer; the qualifier's columns are.
        if (context.IsAfterDot || context.IsVariableContext)
        {
            yield break;
        }

        var preferred = PreferredFor(context.Clause);

        foreach (var keyword in SqlLanguage.Keywords)
        {
            yield return new CompletionItem
            {
                Label = keyword,
                Kind = CompletionItemKind.Keyword,
                Detail = "keyword",
                ProviderId = ProviderId,
                SortRank = preferred.Contains(keyword) ? 20 : 300,
            };
        }

        foreach (var function in SqlLanguage.Functions)
        {
            yield return new CompletionItem
            {
                Label = function,
                InsertText = function + "(",
                Kind = CompletionItemKind.Function,
                Detail = "function",
                ProviderId = ProviderId,

                // Functions belong in projections and predicates, not where a table name goes.
                SortRank = context.Clause is SqlClause.Select or SqlClause.Where
                    or SqlClause.Having or SqlClause.On or SqlClause.OrderBy ? 120 : 340,
            };
        }

        foreach (var type in SqlLanguage.DataTypes)
        {
            yield return new CompletionItem
            {
                Label = type,
                Kind = CompletionItemKind.DataType,
                Detail = "type",
                ProviderId = ProviderId,
                SortRank = context.Clause is SqlClause.Declare ? 20 : 400,
            };
        }

        // Snippets are only offered on an explicit invoke: unasked-for multi-line inserts
        // arriving from type-ahead are how an editor earns a reputation for fighting you.
        if (!context.ExplicitlyInvoked)
        {
            yield break;
        }

        foreach (var (label, insert) in SqlLanguage.Snippets)
        {
            yield return new CompletionItem
            {
                Label = label,
                InsertText = insert,
                Kind = CompletionItemKind.Snippet,
                Detail = "snippet",
                ProviderId = ProviderId,
                SortRank = 200,
            };
        }
    }

    private static IReadOnlySet<string> PreferredFor(SqlClause clause) => clause switch
    {
        SqlClause.Select => Set("DISTINCT", "TOP", "AS", "CASE", "WHEN", "THEN", "ELSE", "END", "FROM", "OVER"),
        SqlClause.From => Set("AS", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "JOIN", "WHERE", "WITH"),
        SqlClause.Join => Set("AS", "ON"),
        SqlClause.On => Set("AND", "OR", "IS", "NULL", "NOT"),
        SqlClause.Where => Set("AND", "OR", "NOT", "IN", "EXISTS", "BETWEEN", "LIKE", "IS", "NULL", "GROUP", "ORDER"),
        SqlClause.GroupBy => Set("BY", "HAVING", "ORDER"),
        SqlClause.OrderBy => Set("BY", "ASC", "DESC", "OFFSET"),
        SqlClause.Having => Set("AND", "OR", "NOT", "ORDER"),
        SqlClause.Insert => Set("INTO", "VALUES", "SELECT"),
        SqlClause.Update => Set("SET", "FROM", "WHERE"),
        SqlClause.Set => Set("WHERE", "FROM"),
        SqlClause.Declare => Set("AS", "TABLE"),
        _ => Set("SELECT", "INSERT", "UPDATE", "DELETE", "WITH", "DECLARE", "MERGE"),
    };

    private static IReadOnlySet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
}
