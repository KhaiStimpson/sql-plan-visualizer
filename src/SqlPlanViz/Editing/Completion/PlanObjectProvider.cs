using SqlPlanViz.Model;

namespace SqlPlanViz.Editing.Completion;

/// <summary>
/// Completions harvested from the loaded plan itself: the tables it touches, the indexes it
/// used, and every column it carried in an operator's output list.
///
/// This is what makes the plan's promise of an offline editor real. A <c>.sqlplan</c> opened
/// with no server names every object the query referenced, so the names you most need are
/// available with nothing connected — and they are better ranked than the catalog's would be,
/// because they are exactly the objects this query is about.
/// </summary>
public sealed class PlanObjectProvider : ICompletionProvider
{
    public const string ProviderId = "plan-objects";

    private sealed record HarvestedTable(string Schema, string Name)
    {
        public string Display => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
    }

    private readonly List<HarvestedTable> _tables = [];
    private readonly Dictionary<string, List<string>> _columnsByQualifier = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _allColumns = [];
    private readonly List<(string Name, string Table)> _indexes = [];
    private readonly List<string> _parameters = [];

    public string Id => ProviderId;

    public string DisplayName => "Objects from the loaded plan";

    public bool IsEnabled { get; set; } = true;

    /// <summary>Re-harvests from a statement. Passing null clears, so unloading a plan is honest.</summary>
    public void Load(PlanStatement? statement)
    {
        _tables.Clear();
        _columnsByQualifier.Clear();
        _allColumns.Clear();
        _indexes.Clear();
        _parameters.Clear();

        if (statement is null)
        {
            return;
        }

        foreach (var node in statement.AllNodes)
        {
            HarvestObjectName(node.ObjectName);

            foreach (var output in node.OutputList)
            {
                HarvestColumn(output);
            }
        }

        foreach (var parameter in statement.Summary.Parameters)
        {
            if (!string.IsNullOrEmpty(parameter.Name) && !_parameters.Contains(parameter.Name, StringComparer.OrdinalIgnoreCase))
            {
                _parameters.Add(parameter.Name);
            }
        }

        // Missing-index findings name columns the plan never output; they are still real
        // columns of a real table and are exactly what you are about to type.
        foreach (var suggestion in statement.MissingIndexes)
        {
            var qualifier = suggestion.Table.Trim('[', ']');
            foreach (var column in suggestion.EqualityColumns
                         .Concat(suggestion.InequalityColumns)
                         .Concat(suggestion.IncludedColumns))
            {
                AddColumn(qualifier, column.Trim('[', ']'));
            }
        }
    }

    /// <summary>
    /// <see cref="Parsing.ShowplanParser"/> formats an object as "schema.table AS alias.index",
    /// with the schema and the index both optional. Unpicking it here keeps the parser's
    /// display format as the single definition of that shape.
    /// </summary>
    private void HarvestObjectName(string? objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName))
        {
            return;
        }

        var text = objectName;
        var alias = string.Empty;
        var asIndex = text.IndexOf(" AS ", StringComparison.OrdinalIgnoreCase);
        if (asIndex >= 0)
        {
            var tail = text[(asIndex + 4)..];
            text = text[..asIndex];

            // The index, when present, is dotted onto the alias rather than the table.
            var dot = tail.IndexOf('.');
            if (dot >= 0)
            {
                alias = tail[..dot];
                AddIndex(tail[(dot + 1)..], text);
            }
            else
            {
                alias = tail;
            }
        }

        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return;
        }

        // Without an alias a trailing part may be an index rather than the table. Showplan
        // gives at most schema.table.index, so three parts means the last one is the index.
        if (alias.Length == 0 && parts.Length >= 3)
        {
            AddIndex(parts[^1], string.Join('.', parts[..^1]));
            parts = parts[..^1];
        }

        var table = new HarvestedTable(
            parts.Length > 1 ? parts[^2] : string.Empty,
            parts[^1]);

        if (string.IsNullOrEmpty(table.Name))
        {
            return;
        }

        if (!_tables.Any(t => string.Equals(t.Display, table.Display, StringComparison.OrdinalIgnoreCase)))
        {
            _tables.Add(table);
        }
    }

    private void AddIndex(string name, string table)
    {
        name = name.Trim('[', ']');
        if (name.Length > 0 && !_indexes.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            _indexes.Add((name, table));
        }
    }

    private void HarvestColumn(string output)
    {
        var dot = output.LastIndexOf('.');
        var qualifier = dot > 0 ? output[..dot] : string.Empty;
        var column = dot > 0 ? output[(dot + 1)..] : output;
        AddColumn(qualifier, column);
    }

    private void AddColumn(string qualifier, string column)
    {
        column = column.Trim('[', ']');

        // Showplan invents names for computed and internal columns; offering Expr1002 as a
        // completion would be actively misleading.
        if (column.Length == 0 || column.StartsWith("Expr", StringComparison.Ordinal) || column.StartsWith("Uniq", StringComparison.Ordinal))
        {
            return;
        }

        if (!_allColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
        {
            _allColumns.Add(column);
        }

        if (qualifier.Length == 0)
        {
            return;
        }

        if (!_columnsByQualifier.TryGetValue(qualifier, out var list))
        {
            list = [];
            _columnsByQualifier[qualifier] = list;
        }

        if (!list.Contains(column, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(column);
        }
    }

    public IEnumerable<CompletionItem> GetItems(CompletionContext context)
    {
        if (context.IsVariableContext)
        {
            foreach (var parameter in _parameters)
            {
                yield return new CompletionItem
                {
                    Label = parameter,
                    Kind = CompletionItemKind.Variable,
                    Detail = "plan parameter",
                    ProviderId = ProviderId,
                    SortRank = 10,
                };
            }

            yield break;
        }

        if (context.IsAfterDot)
        {
            foreach (var item in ColumnsFor(context))
            {
                yield return item;
            }

            yield break;
        }

        // Aliases the statement already declares beat everything: after typing "o" in a WHERE
        // clause of a query that says "FROM dbo.Orders AS o", "o" is the answer.
        foreach (var table in context.TablesInScope)
        {
            if (string.IsNullOrEmpty(table.Alias))
            {
                continue;
            }

            yield return new CompletionItem
            {
                Label = table.Alias,
                Kind = CompletionItemKind.Alias,
                Detail = table.Display,
                ProviderId = ProviderId,
                SortRank = 1,
            };
        }

        var wantsTables = context.Clause is SqlClause.From or SqlClause.Join
            or SqlClause.Into or SqlClause.Update or SqlClause.Unknown;

        foreach (var table in _tables)
        {
            yield return new CompletionItem
            {
                Label = table.Display,
                Kind = CompletionItemKind.Table,
                Detail = "in the loaded plan",
                ProviderId = ProviderId,
                SortRank = wantsTables ? 5 : 60,
            };
        }

        if (context.Clause is SqlClause.From or SqlClause.Join)
        {
            // Index names only make sense in a hint, which is a FROM-clause construct.
            foreach (var (name, table) in _indexes)
            {
                yield return new CompletionItem
                {
                    Label = name,
                    Kind = CompletionItemKind.Index,
                    Detail = $"index on {table}",
                    ProviderId = ProviderId,
                    SortRank = 80,
                };
            }
        }

        foreach (var column in _allColumns)
        {
            yield return new CompletionItem
            {
                Label = column,
                Kind = CompletionItemKind.Column,
                Detail = OwnerOf(column),
                ProviderId = ProviderId,
                SortRank = wantsTables ? 70 : 10,
            };
        }
    }

    private IEnumerable<CompletionItem> ColumnsFor(CompletionContext context)
    {
        var qualifier = context.Qualifier;
        var names = new List<string>();

        if (_columnsByQualifier.TryGetValue(qualifier, out var direct))
        {
            names.AddRange(direct);
        }

        // The plan qualifies columns by the alias the *original* query used. If the editor's
        // alias differs, fall back to the table the alias resolves to.
        if (context.QualifiedTable is { } table)
        {
            foreach (var key in new[] { table.Name, table.Display, table.Alias })
            {
                if (!string.IsNullOrEmpty(key) && _columnsByQualifier.TryGetValue(key, out var viaTable))
                {
                    names.AddRange(viaTable.Except(names, StringComparer.OrdinalIgnoreCase));
                }
            }
        }

        foreach (var column in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return new CompletionItem
            {
                Label = column,
                Kind = CompletionItemKind.Column,
                Detail = qualifier,
                ProviderId = ProviderId,
                SortRank = 1,
            };
        }
    }

    private string OwnerOf(string column)
    {
        foreach (var (qualifier, columns) in _columnsByQualifier)
        {
            if (columns.Contains(column, StringComparer.OrdinalIgnoreCase))
            {
                return qualifier;
            }
        }

        return "column";
    }
}
