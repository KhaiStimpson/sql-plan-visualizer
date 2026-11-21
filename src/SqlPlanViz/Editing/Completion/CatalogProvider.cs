using SqlPlanViz.Capture;

namespace SqlPlanViz.Editing.Completion;

/// <summary>
/// Completions from the connected database's real schema
/// (live-plan-editor-plan.md Phase 6).
///
/// Reads only the snapshot handed to it — the round trip happens once, in
/// <see cref="CatalogMetadataService"/>, and never on a keystroke. With no snapshot the
/// provider returns nothing and the offline providers carry on unaffected, which is what
/// "each degrading independently" means in practice.
/// </summary>
public sealed class CatalogProvider : ICompletionProvider
{
    public const string ProviderId = "catalog";

    public string Id => ProviderId;

    public string DisplayName => "Tables and columns from the connected database";

    public bool IsEnabled { get; set; } = true;

    public CatalogSnapshot Snapshot { get; set; } = CatalogSnapshot.Empty;

    public IEnumerable<CompletionItem> GetItems(CompletionContext context)
    {
        if (Snapshot.IsEmpty || context.IsVariableContext)
        {
            yield break;
        }

        if (context.IsAfterDot)
        {
            foreach (var item in AfterDot(context))
            {
                yield return item;
            }

            yield break;
        }

        var wantsTables = context.Clause is SqlClause.From or SqlClause.Join
            or SqlClause.Into or SqlClause.Update or SqlClause.Unknown;

        foreach (var table in Snapshot.Tables)
        {
            // Schema-qualified, because that is what should be written; dbo tables also get
            // their bare name, because that is what people actually type.
            yield return new CompletionItem
            {
                Label = table.QualifiedName,
                Kind = table.IsView ? CompletionItemKind.View : CompletionItemKind.Table,
                Detail = DescribeTable(table),
                ProviderId = ProviderId,
                SortRank = wantsTables ? 30 : 200,
            };

            if (string.Equals(table.Schema, "dbo", StringComparison.OrdinalIgnoreCase))
            {
                yield return new CompletionItem
                {
                    Label = table.Name,
                    InsertText = table.QualifiedName,
                    Kind = table.IsView ? CompletionItemKind.View : CompletionItemKind.Table,
                    Detail = DescribeTable(table),
                    ProviderId = ProviderId,
                    SortRank = wantsTables ? 35 : 210,
                };
            }
        }

        if (context.Clause is SqlClause.Declare)
        {
            foreach (var type in Snapshot.TableTypes)
            {
                yield return new CompletionItem
                {
                    Label = type.QualifiedName,
                    Kind = CompletionItemKind.TableType,
                    Detail = $"table type  ·  {type.Columns.Count} columns",
                    ProviderId = ProviderId,
                    SortRank = 25,
                };
            }
        }

        // Columns of the tables the statement already has in scope. Everything else in the
        // database would drown them.
        foreach (var scope in context.TablesInScope)
        {
            if (Snapshot.FindTable(scope.Display) is not { } table)
            {
                continue;
            }

            foreach (var column in table.Columns)
            {
                yield return new CompletionItem
                {
                    Label = column.Name,
                    InsertText = context.TablesInScope.Count > 1 && scope.Qualifier.Length > 0
                        ? $"{scope.Qualifier}.{column.Name}"
                        : column.Name,
                    Kind = CompletionItemKind.Column,
                    Detail = $"{scope.Qualifier}  ·  {column.Detail}",
                    ProviderId = ProviderId,
                    SortRank = wantsTables ? 90 : 15,
                };
            }
        }

        foreach (var schema in Snapshot.Schemas)
        {
            yield return new CompletionItem
            {
                Label = schema,
                InsertText = schema + ".",
                Kind = CompletionItemKind.Schema,
                Detail = "schema",
                ProviderId = ProviderId,
                SortRank = wantsTables ? 120 : 320,
            };
        }
    }

    private IEnumerable<CompletionItem> AfterDot(CompletionContext context)
    {
        // "o." where o is an alias in scope, or "dbo.Orders." — both resolve to a table whose
        // columns are the only sensible thing to offer.
        var table = context.QualifiedTable is { } scoped
            ? Snapshot.FindTable(scoped.Display) ?? Snapshot.FindTable(scoped.Name)
            : Snapshot.FindTable(context.Qualifier);

        if (table is not null)
        {
            foreach (var column in table.Columns)
            {
                yield return new CompletionItem
                {
                    Label = column.Name,
                    Kind = CompletionItemKind.Column,
                    Detail = column.Detail,
                    ProviderId = ProviderId,
                    SortRank = 1,
                };
            }

            yield break;
        }

        // "dbo." — a schema rather than a table.
        if (!Snapshot.Schemas.Any(s => string.Equals(s, context.Qualifier, StringComparison.OrdinalIgnoreCase)))
        {
            yield break;
        }

        foreach (var candidate in Snapshot.Tables.Where(t =>
                     string.Equals(t.Schema, context.Qualifier, StringComparison.OrdinalIgnoreCase)))
        {
            yield return new CompletionItem
            {
                Label = candidate.Name,
                Kind = candidate.IsView ? CompletionItemKind.View : CompletionItemKind.Table,
                Detail = DescribeTable(candidate),
                ProviderId = ProviderId,
                SortRank = 1,
            };
        }

        // A schema-qualified name in a DECLARE is a table type, not a table — "dbo." after
        // AS is how every table-valued parameter is written.
        foreach (var type in Snapshot.TableTypes.Where(t =>
                     string.Equals(t.Schema, context.Qualifier, StringComparison.OrdinalIgnoreCase)))
        {
            yield return new CompletionItem
            {
                Label = type.Name,
                Kind = CompletionItemKind.TableType,
                Detail = $"table type  ·  {type.Columns.Count} columns",
                ProviderId = ProviderId,
                SortRank = context.Clause is SqlClause.Declare ? 0 : 40,
            };
        }
    }

    private static string DescribeTable(CatalogTable table)
    {
        var parts = new List<string> { table.IsView ? "view" : "table", $"{table.Columns.Count} columns" };

        if (table.Indexes.Count > 0)
        {
            parts.Add($"{table.Indexes.Count} indexes");
        }

        return string.Join("  ·  ", parts);
    }
}
