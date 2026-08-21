using System.Text.RegularExpressions;
using SqlPlanViz.Capture;
using SqlPlanViz.Diagnostics;
using SqlPlanViz.Model;

namespace SqlPlanViz.Editing.Completion;

/// <summary>
/// Completions that know what is wrong with the plan
/// (live-plan-editor-plan.md Phase 6).
///
/// The other three providers answer "what is this called". This one answers "what should I
/// write instead", drawing on the diagnostics layer: the columns of an active missing-index
/// finding, the SARGable rewrite the non-sargable-predicate rule already worked out, and the
/// explicit column list that replaces a <c>SELECT *</c>.
///
/// Its items are ranked above everything else and marked as suggestions, because a suggestion
/// that looks like a schema object is worse than no suggestion — it reads as the database
/// offering a column that does not exist.
/// </summary>
public sealed class TuningProvider : ICompletionProvider
{
    public const string ProviderId = "tuning";

    /// <summary>Suggestions sort above every other provider's items, by construction.</summary>
    private const int SuggestionRank = 0;

    private static readonly Regex RewriteAfter = new(@"After:\s*(?<after>.+)", RegexOptions.IgnoreCase);

    private PlanStatement? _statement;

    public string Id => ProviderId;

    public string DisplayName => "Suggestions from the plan's diagnostics";

    public bool IsEnabled { get; set; } = true;

    public CatalogSnapshot Snapshot { get; set; } = CatalogSnapshot.Empty;

    public void Load(PlanStatement? statement) => _statement = statement;

    public IEnumerable<CompletionItem> GetItems(CompletionContext context)
    {
        if (_statement is null || context.IsVariableContext || context.IsAfterDot)
        {
            yield break;
        }

        foreach (var item in MissingIndexColumns(context))
        {
            yield return item;
        }

        foreach (var item in SargableRewrites(context))
        {
            yield return item;
        }

        foreach (var item in ExpandStar(context))
        {
            yield return item;
        }
    }

    /// <summary>
    /// The columns of a missing-index finding, offered where an index would be used: equality
    /// columns in a predicate, inequality columns in a range, included columns in the
    /// projection. Offering all of them everywhere would be noise dressed as advice.
    /// </summary>
    private IEnumerable<CompletionItem> MissingIndexColumns(CompletionContext context)
    {
        if (context.Clause is not (SqlClause.Where or SqlClause.On or SqlClause.Select
            or SqlClause.OrderBy or SqlClause.GroupBy or SqlClause.Having))
        {
            yield break;
        }

        foreach (var suggestion in _statement!.MissingIndexes)
        {
            var target = suggestion.Table.Trim('[', ']');

            // Only for a table the statement being edited actually references — a finding
            // about a table the edit removed is not advice about this query any more.
            if (context.TablesInScope.Count > 0
                && !context.TablesInScope.Any(t => string.Equals(t.Name, target, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var relevant = context.Clause switch
            {
                SqlClause.Where or SqlClause.On or SqlClause.Having =>
                    suggestion.EqualityColumns.Concat(suggestion.InequalityColumns),
                SqlClause.OrderBy or SqlClause.GroupBy => suggestion.InequalityColumns.Concat(suggestion.EqualityColumns),
                _ => suggestion.IncludedColumns,
            };

            foreach (var column in relevant.Select(c => c.Trim('[', ']')).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                yield return new CompletionItem
                {
                    Label = column,
                    Kind = CompletionItemKind.TuningSuggestion,
                    IsSuggestion = true,
                    Detail = $"covering index column on {target}",
                    Documentation = $"The plan reports a missing index on {target} with {suggestion.ImpactText} "
                                    + $"estimated impact. Its columns are {suggestion.ColumnSummary}.",
                    ProviderId = ProviderId,
                    SortRank = SuggestionRank,
                };
            }
        }
    }

    /// <summary>
    /// The rewrite the non-sargable-predicate rule already computed. It writes the fix as
    /// "Before: … / After: …", and the After line is exactly what belongs in the editor.
    /// </summary>
    private IEnumerable<CompletionItem> SargableRewrites(CompletionContext context)
    {
        if (context.Clause is not (SqlClause.Where or SqlClause.On or SqlClause.Having))
        {
            yield break;
        }

        foreach (var finding in _statement!.Findings.Where(f => f.RuleId == "non-sargable-predicate"))
        {
            foreach (var fix in finding.Fixes.Where(f => f.Kind == FixKind.Rewrite && f.Snippet is not null))
            {
                var match = RewriteAfter.Match(fix.Snippet!);
                if (!match.Success)
                {
                    continue;
                }

                var after = match.Groups["after"].Value.Trim();
                if (after.Length == 0)
                {
                    continue;
                }

                // The rewrite text carries Showplan's bracketed four-part column names; a
                // person writing this predicate would use the alias in scope.
                var rewritten = Simplify(after, context);

                yield return new CompletionItem
                {
                    Label = rewritten,
                    Kind = CompletionItemKind.TuningSuggestion,
                    IsSuggestion = true,
                    Detail = "SARGable rewrite",
                    Documentation = finding.Title + " — " + fix.Summary,
                    ProviderId = ProviderId,
                    SortRank = SuggestionRank,
                };
            }
        }
    }

    /// <summary>
    /// Replaces a <c>SELECT *</c> with the columns it actually resolves to. Catalog columns
    /// where there is a connection, the plan's own output list where there is not.
    /// </summary>
    private IEnumerable<CompletionItem> ExpandStar(CompletionContext context)
    {
        if (context.Clause is not SqlClause.Select)
        {
            yield break;
        }

        // The star immediately before the caret, ignoring whitespace — this only fires where
        // the user is actually standing on one.
        var i = context.ReplaceStart;
        while (i > 0 && context.Text[i - 1] is ' ' or '\t')
        {
            i--;
        }

        if (i == 0 || context.Text[i - 1] != '*')
        {
            yield break;
        }

        var starOffset = i - 1;
        var columns = ColumnsInScope(context).ToList();
        if (columns.Count == 0)
        {
            yield break;
        }

        var expansion = string.Join(", ", columns);
        yield return new CompletionItem
        {
            Label = "Expand * to explicit columns",
            InsertText = expansion,
            Kind = CompletionItemKind.TuningSuggestion,
            IsSuggestion = true,
            Detail = $"{columns.Count} columns",
            Documentation = "SELECT * carries every column through every operator, widens the rows a "
                            + "join has to copy, and quietly defeats a covering index. Expanding it: " + expansion,
            ProviderId = ProviderId,
            SortRank = SuggestionRank,
            ReplaceStartOverride = starOffset,
            ReplaceLengthOverride = context.CaretOffset - starOffset,
        };
    }

    private IEnumerable<string> ColumnsInScope(CompletionContext context)
    {
        var qualify = context.TablesInScope.Count > 1;

        foreach (var scope in context.TablesInScope)
        {
            var table = Snapshot.FindTable(scope.Display) ?? Snapshot.FindTable(scope.Name);
            if (table is not null)
            {
                foreach (var column in table.Columns)
                {
                    yield return qualify && scope.Qualifier.Length > 0
                        ? $"{scope.Qualifier}.{column.Name}"
                        : column.Name;
                }
            }
        }

        // No catalog: the plan's own output list is the next best answer, and it is exactly
        // the set of columns this query already carries.
        if (context.TablesInScope.Any(t => Snapshot.FindTable(t.Display) is not null))
        {
            yield break;
        }

        var fromPlan = _statement!.Root.OutputList
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(o => !o.Contains("Expr", StringComparison.Ordinal));

        foreach (var column in fromPlan)
        {
            yield return column;
        }
    }

    /// <summary>
    /// Turns the rule's fix text into something directly insertable: Showplan's bracketed
    /// four-part names become the alias in scope, and the rule's <c>&lt;(2024)+1&gt;</c>
    /// arithmetic placeholders become the numbers they stand for. A suggestion the user has
    /// to hand-edit before it compiles is barely a suggestion.
    /// </summary>
    private static string Simplify(string expression, CompletionContext context)
    {
        var text = Regex.Replace(expression, @"(?:\[[^\]]+\]\.)*\[(?<column>[^\]]+)\]", match =>
        {
            var column = match.Groups["column"].Value;
            var owner = context.TablesInScope.FirstOrDefault();
            return context.TablesInScope.Count == 1 && owner is { Qualifier.Length: > 0 }
                ? $"{owner.Qualifier}.{column}"
                : column;
        });

        return Regex.Replace(text, @"<\(?(?<value>-?\d+)\)?(?<add>\s*\+\s*(?<addend>\d+))?>", match =>
        {
            if (!long.TryParse(match.Groups["value"].Value, out var value))
            {
                return match.Value;
            }

            if (match.Groups["add"].Success && long.TryParse(match.Groups["addend"].Value, out var addend))
            {
                value += addend;
            }

            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        });
    }
}
