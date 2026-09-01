using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlPlanViz.Editing;
using SqlPlanViz.Model;
using SqlPlanViz.Sql;

namespace SqlPlanViz.Diagnostics;

/// <summary>
/// A span of the statement text an operator most likely came from.
///
/// <see cref="Confidence"/> exists because this is inference, not fact: Showplan carries no
/// source offsets, and a confidently wrong arrow is worse than no arrow. Callers that draw
/// something persistent — the gutter marks, the inline annotations — are expected to have a
/// threshold and draw nothing below it.
/// </summary>
public sealed record SqlTextSpan(int Start, int Length, string Clause)
{
    /// <summary>0 to 1. An exact table reference scores high; a whole-clause guess scores low.</summary>
    public double Confidence { get; init; } = 1.0;
}

/// <summary>
/// Where each clause, table reference and column reference of a statement starts and ends,
/// taken from the ScriptDom AST (live-plan-editor-plan.md Phase 5).
///
/// Building this once and reusing it is the point: mapping forty operators to lines would
/// otherwise re-parse the batch forty times.
/// </summary>
public sealed class SqlAstIndex
{
    internal sealed record Region(string Clause, int Start, int Length);

    internal sealed record TableRegion(string Schema, string Name, string Alias, int Start, int Length);

    internal sealed record ColumnRegion(string Qualifier, string Name, int Start, int Length);

    internal List<Region> Clauses { get; } = [];

    internal List<TableRegion> Tables { get; } = [];

    internal List<ColumnRegion> Columns { get; } = [];

    /// <summary>Returns null when the batch does not parse, which is the caller's cue to fall back.</summary>
    public static SqlAstIndex? Build(string sql, SqlParserVersion? parserVersion = null)
    {
        var fragment = TSqlParserFactory.TryParse(sql, out _, parserVersion);
        if (fragment is null)
        {
            return null;
        }

        var index = new SqlAstIndex();
        fragment.Accept(new IndexVisitor(index));
        return index;
    }

    private sealed class IndexVisitor(SqlAstIndex index) : TSqlFragmentVisitor
    {
        public override void Visit(QuerySpecification node)
        {
            if (node.SelectElements.Count > 0)
            {
                var first = node.SelectElements[0];
                var last = node.SelectElements[^1];
                Add("SELECT", first.StartOffset, last.StartOffset + last.FragmentLength - first.StartOffset);
            }

            AddFragment("FROM", node.FromClause);
            AddFragment("WHERE", node.WhereClause);
            AddFragment("GROUP BY", node.GroupByClause);
            AddFragment("HAVING", node.HavingClause);
        }

        public override void Visit(QueryExpression node) => AddFragment("ORDER BY", node.OrderByClause);

        public override void Visit(SelectStatement node) => AddFragment("ORDER BY", node.QueryExpression?.OrderByClause);

        public override void Visit(QualifiedJoin node)
        {
            AddFragment("JOIN", node.SecondTableReference);
            AddFragment("ON", node.SearchCondition);
        }

        public override void Visit(UnqualifiedJoin node) => AddFragment("JOIN", node.SecondTableReference);

        public override void Visit(InsertSpecification node) => AddFragment("INSERT", node.Target);

        public override void Visit(UpdateSpecification node)
        {
            AddFragment("UPDATE", node.Target);
            AddFragment("WHERE", node.WhereClause);
        }

        public override void Visit(DeleteSpecification node)
        {
            AddFragment("DELETE", node.Target);
            AddFragment("WHERE", node.WhereClause);
        }

        public override void Visit(NamedTableReference node)
        {
            var name = node.SchemaObject?.BaseIdentifier?.Value;
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            index.Tables.Add(new SqlAstIndex.TableRegion(
                node.SchemaObject?.SchemaIdentifier?.Value ?? string.Empty,
                name,
                node.Alias?.Value ?? string.Empty,
                node.StartOffset,
                node.FragmentLength));
        }

        public override void Visit(ColumnReferenceExpression node)
        {
            var identifiers = node.MultiPartIdentifier?.Identifiers;
            if (identifiers is not { Count: > 0 })
            {
                return;
            }

            index.Columns.Add(new SqlAstIndex.ColumnRegion(
                identifiers.Count > 1 ? identifiers[^2].Value : string.Empty,
                identifiers[^1].Value,
                node.StartOffset,
                node.FragmentLength));
        }

        private void AddFragment(string clause, TSqlFragment? fragment)
        {
            if (fragment is not null)
            {
                Add(clause, fragment.StartOffset, fragment.FragmentLength);
            }
        }

        private void Add(string clause, int start, int length)
        {
            if (start >= 0 && length > 0)
            {
                index.Clauses.Add(new SqlAstIndex.Region(clause, start, length));
            }
        }
    }
}

/// <summary>
/// Maps an operator back to the piece of SQL it most likely came from.
///
/// Showplan carries no source spans, so this works in two tiers. When the batch parses, the
/// ScriptDom AST says exactly where a table reference and a clause begin and end, so "this Key
/// Lookup is the dbo.Orders reference on line 4" stops being a guess about where a substring
/// happened to occur. When it does not parse — which is most of them, most of the time, while
/// someone is typing — clause scoring takes over: every clause is scored against the evidence
/// the operator does carry (the alias and table it touches, the columns, parameters and
/// literals in its predicates, the columns it outputs) and the best explanation wins.
///
/// Scoring rather than first-match matters in that fallback: a plan says
/// <c>[Db].[dbo].[Orders].[CustomerId] as [o].[CustomerId]</c>, and <c>CustomerId</c> alone
/// appears in three clauses of a typical query. The alias-qualified form
/// (<c>o.CustomerId</c>), the parameter and the literal are what actually pin down which one,
/// so those weigh most.
///
/// When nothing matches and the operator has no clause-level meaning either — a Parallelism
/// exchange has no SQL to point at — this returns null rather than guessing. A confident wrong
/// highlight is worse than none.
/// </summary>
public static partial class SqlNodeMapper
{
    /// <summary>Below this, the caller should draw nothing rather than a wrong arrow.</summary>
    public const double MinimumUsefulConfidence = 0.5;

    private const double TableReferenceConfidence = 0.9;
    private const double UniqueColumnConfidence = 0.7;
    private const double AmbiguousColumnConfidence = 0.45;
    private const double ClauseOnlyConfidence = 0.35;
    /// <summary>What the clause-scoring fallback scores; deliberately below the threshold.</summary>
    private const double FallbackConfidence = 0.3;

    /// <summary>How much the operator's expected clause counts when the text itself is ambiguous.</summary>
    private const int PreferredClauseBonus = 3;

    private const int AliasColumnWeight = 4;
    private const int ParameterWeight = 4;
    private const int LiteralWeight = 4;
    private const int TableWeight = 3;
    private const int QualifiedColumnWeight = 3;
    private const int AliasWeight = 2;
    private const int NumberWeight = 2;
    private const int ColumnWeight = 1;
    private const int OutputColumnWeight = 1;

    /// <summary>An output list can be dozens of columns wide; it is a hint, not the whole vote.</summary>
    private const int MaxOutputClues = 12;

    private readonly record struct Clue(string Text, int Weight);

    private static readonly string[] JoinDirections = ["Left", "Right", "Full"];

    public static SqlTextSpan? Map(string sql, PlanNode node) => Map(sql, node, index: null);

    /// <summary>
    /// Maps one operator. Pass a prebuilt <paramref name="index"/> when mapping many
    /// operators against the same text; leave it null and one is built for this call.
    /// </summary>
    public static SqlTextSpan? Map(string sql, PlanNode node, SqlAstIndex? index)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return null;
        }

        index ??= SqlAstIndex.Build(sql);
        return index is null ? MapByClauseScoring(sql, node) : MapByAst(sql, node, index);
    }

    private static SqlTextSpan? MapByAst(string sql, PlanNode node, SqlAstIndex index)
    {
        // Operators that exist only to move rows around have no clause of their own, and the
        // AST offers nothing to override that with: an exchange is not the FROM clause it sits
        // above. Those map to nothing rather than to a confident wrong span.
        if (PreferredClause(node) is not { } clause)
        {
            return null;
        }

        // 1. The operator names a table. The AST knows exactly where that table is written.
        if (ResolveTable(node.ObjectName, index) is { } table)
        {
            return new SqlTextSpan(table.Start, table.Length, clause) { Confidence = TableReferenceConfidence };
        }

        // 2. Its predicate names a column. One occurrence is a near-certainty; several mean
        //    the operator could belong to any of them, and the score has to say so.
        foreach (var identifier in PredicateIdentifiers(node))
        {
            var matches = index.Columns
                .Where(c => string.Equals(c.Name, identifier, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                continue;
            }

            // Prefer an occurrence inside the clause this operator belongs to — a Sort's
            // column is far more likely to be the one in ORDER BY than the one in SELECT.
            var region = index.Clauses.FirstOrDefault(r => r.Clause == clause);
            var preferred = region is null
                ? matches[0]
                : matches.FirstOrDefault(c => c.Start >= region.Start && c.Start < region.Start + region.Length)
                  ?? matches[0];

            return new SqlTextSpan(preferred.Start, preferred.Length, clause)
            {
                Confidence = matches.Count == 1 ? UniqueColumnConfidence : AmbiguousColumnConfidence,
            };
        }

        // 3. Nothing but the operator's kind to go on: point at the clause and score it low,
        //    which is below the threshold the gutter draws at.
        if (index.Clauses.FirstOrDefault(r => r.Clause == clause) is { } fallback)
        {
            return new SqlTextSpan(fallback.Start, fallback.Length, clause) { Confidence = ClauseOnlyConfidence };
        }

        return MapByClauseScoring(sql, node);
    }

    private static SqlAstIndex.TableRegion? ResolveTable(string? objectName, SqlAstIndex index)
    {
        if (string.IsNullOrWhiteSpace(objectName))
        {
            return null;
        }

        // Showplan writes "schema.table AS alias.index"; the alias is the strongest key,
        // because a query can reference the same table twice under different aliases.
        var text = objectName;
        var alias = string.Empty;
        var asIndex = text.IndexOf(" AS ", StringComparison.OrdinalIgnoreCase);
        if (asIndex >= 0)
        {
            var tail = text[(asIndex + 4)..];
            text = text[..asIndex];
            var dot = tail.IndexOf('.');
            alias = (dot >= 0 ? tail[..dot] : tail).Trim('[', ']');
        }

        if (alias.Length > 0)
        {
            var byAlias = index.Tables.FirstOrDefault(t =>
                string.Equals(t.Alias, alias, StringComparison.OrdinalIgnoreCase));
            if (byAlias is not null)
            {
                return byAlias;
            }
        }

        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim('[', ']'))
            .ToList();
        if (parts.Count == 0)
        {
            return null;
        }

        // Without an alias the last part may be an index name rather than the table.
        foreach (var candidate in parts.AsEnumerable().Reverse())
        {
            var match = index.Tables.FirstOrDefault(t =>
                string.Equals(t.Name, candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static IEnumerable<string> PredicateIdentifiers(PlanNode node)
    {
        var expression = node.SeekPredicate ?? node.Predicate;
        if (string.IsNullOrWhiteSpace(expression))
        {
            yield break;
        }

        // Showplan writes columns bracketed and fully qualified; the last part is the column.
        foreach (Match match in BracketedIdentifier().Matches(expression).Cast<Match>())
        {
            var value = match.Groups[1].Value;
            if (value.Length > 0 && !value.StartsWith("@", StringComparison.Ordinal))
            {
                yield return value;
            }
        }
    }

    /// <summary>
    /// The clause an operator belongs to when the text gives no better answer. Operators that
    /// exist only to move rows around (exchanges, spools) deliberately map to nothing.
    /// </summary>
    private static string? PreferredClause(PlanNode node)
    {
        var physical = node.PhysicalOp;
        var logical = node.LogicalOp;

        if (logical.Contains("Aggregate", StringComparison.OrdinalIgnoreCase))
        {
            return "GROUP BY";
        }

        if (logical.Contains("Join", StringComparison.OrdinalIgnoreCase)
            || logical.Contains("Apply", StringComparison.OrdinalIgnoreCase))
        {
            return "JOIN";
        }

        if (logical.Contains("Union", StringComparison.OrdinalIgnoreCase)
            || physical.Contains("Concatenation", StringComparison.OrdinalIgnoreCase))
        {
            return "UNION";
        }

        if (physical.Contains("Sort", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER BY";
        }

        if (physical.Contains("Insert", StringComparison.OrdinalIgnoreCase))
        {
            return "INSERT";
        }

        if (physical.Contains("Update", StringComparison.OrdinalIgnoreCase))
        {
            return "UPDATE";
        }

        if (physical.Contains("Delete", StringComparison.OrdinalIgnoreCase))
        {
            return "DELETE";
        }

        if (physical.Contains("Filter", StringComparison.OrdinalIgnoreCase))
        {
            return "WHERE";
        }

        // A lookup exists to fetch the columns the index did not cover — that is the select list.
        if (physical.Contains("Lookup", StringComparison.OrdinalIgnoreCase)
            || physical.Contains("Compute Scalar", StringComparison.OrdinalIgnoreCase)
            || physical.Contains("Top", StringComparison.OrdinalIgnoreCase))
        {
            return "SELECT";
        }

        if (physical.Contains("Scan", StringComparison.OrdinalIgnoreCase)
            || physical.Contains("Seek", StringComparison.OrdinalIgnoreCase))
        {
            return "FROM";
        }

        return null;
    }

    // ---- Fallback for a batch that does not parse --------------------------

    /// <summary>
    /// Clause scoring over <see cref="SqlClauseSplitter"/>, which needs no parse tree and so
    /// still works on half-typed SQL — losing the highlight entirely while typing would be
    /// worse than a rough one. Everything it returns is scored below
    /// <see cref="MinimumUsefulConfidence"/>, so the gutter marks and the inline annotations
    /// stay silent and only the click-through highlight uses it.
    /// </summary>
    private static SqlTextSpan? MapByClauseScoring(string sql, PlanNode node)
    {
        var regions = SqlClauseSplitter.Split(sql);
        if (regions.Count == 0)
        {
            return null;
        }

        var preferred = PreferredClause(node);

        // Every operator outputs the columns flowing through it, so an output list mostly echoes
        // the select list — evidence only where producing those columns is the operator's whole
        // job (a lookup, a compute scalar). Elsewhere it would outvote the real signals.
        var clues = CollectClues(node, includeOutputList: preferred == "SELECT");

        SqlClauseRegion? best = null;
        var bestScore = 0;

        foreach (var region in regions)
        {
            var score = 0;
            foreach (var clue in clues)
            {
                if (ContainsWord(region.SearchText, clue.Text))
                {
                    score += clue.Weight;
                }
            }

            var isPreferred = preferred is not null && region.Kind == preferred;
            if (score == 0 && !isPreferred)
            {
                continue;
            }

            var total = score + (isPreferred ? PreferredClauseBonus : 0);
            if (total > bestScore)
            {
                bestScore = total;
                best = region;
            }
        }

        return best is null
            ? null
            : new SqlTextSpan(best.Start, best.Length, best.Kind) { Confidence = FallbackConfidence };
    }

    private static List<Clue> CollectClues(PlanNode node, bool includeOutputList)
    {
        var clues = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Add(string? text, int weight)
        {
            if (string.IsNullOrWhiteSpace(text) || IsGeneratedName(text))
            {
                return;
            }

            var key = text.Trim();
            if (!clues.TryGetValue(key, out var existing) || weight > existing)
            {
                clues[key] = weight;
            }
        }

        Add(node.ObjectTable, TableWeight);
        if (!string.Equals(node.ObjectAlias, node.ObjectTable, StringComparison.OrdinalIgnoreCase))
        {
            Add(node.ObjectAlias, AliasWeight);
        }

        // "Left Outer Join" tells us which of several JOIN clauses this is, when the operator
        // carries no predicate of its own to match on.
        foreach (var direction in JoinDirections)
        {
            if (node.LogicalOp.StartsWith(direction, StringComparison.OrdinalIgnoreCase))
            {
                Add(direction, AliasWeight);
            }
        }

        foreach (var predicate in new[] { node.SeekPredicate, node.Predicate })
        {
            if (string.IsNullOrWhiteSpace(predicate))
            {
                continue;
            }

            // [Db].[dbo].[Orders].[OrderDate] as [o].[OrderDate] — the aliased half is the half
            // the query text actually contains.
            foreach (Match match in AliasedColumn().Matches(predicate))
            {
                Add($"{match.Groups[1].Value}.{match.Groups[2].Value}", AliasColumnWeight);
                Add(match.Groups[2].Value, ColumnWeight);
            }

            foreach (Match match in QualifiedName().Matches(predicate))
            {
                var parts = match.Value.Split('.');
                var column = parts[^1].Trim('[', ']');
                Add(column, ColumnWeight);
                if (parts.Length >= 2)
                {
                    Add($"{parts[^2].Trim('[', ']')}.{column}", QualifiedColumnWeight);
                }
            }

            foreach (Match match in Parameter().Matches(predicate))
            {
                Add(match.Groups[1].Value, ParameterWeight);
            }

            foreach (Match match in StringLiteral().Matches(predicate))
            {
                Add(match.Value, LiteralWeight);
            }

            foreach (Match match in NumericLiteral().Matches(predicate))
            {
                Add(match.Groups[1].Value, NumberWeight);
            }
        }

        if (includeOutputList)
        {
            foreach (var column in node.OutputList.Take(MaxOutputClues))
            {
                Add(column, OutputColumnWeight);
                var dot = column.LastIndexOf('.');
                if (dot >= 0)
                {
                    Add(column[(dot + 1)..], OutputColumnWeight);
                }
            }
        }

        return clues.Select(pair => new Clue(pair.Key.ToUpperInvariant(), pair.Value)).ToList();
    }

    /// <summary>
    /// Names the optimizer invented (Expr1004, Bmk1000, a CONVERT_IMPLICIT wrapper) exist only
    /// inside the plan. Matching them against SQL text can only produce false positives.
    /// </summary>
    private static bool IsGeneratedName(string text) =>
        GeneratedName().IsMatch(text) || text.Contains("CONVERT_IMPLICIT", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whole-word containment over already-normalised text. A dot is not a word character, so
    /// <c>OrderDate</c> is found inside <c>o.OrderDate</c>, while <c>Orders</c> is not found
    /// inside <c>OrdersHistory</c>.
    /// </summary>
    private static bool ContainsWord(string haystack, string needle)
    {
        if (needle.Length == 0)
        {
            return false;
        }

        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            var before = index == 0 ? ' ' : haystack[index - 1];
            var afterIndex = index + needle.Length;
            var after = afterIndex >= haystack.Length ? ' ' : haystack[afterIndex];

            if (!IsWordChar(before) && !IsWordChar(after))
            {
                return true;
            }

            index = haystack.IndexOf(needle, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';

    [GeneratedRegex(@"\bas\s+\[([^\]]+)\]\.\[([^\]]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex AliasedColumn();

    [GeneratedRegex(@"\[[^\]@]+\](?:\.\[[^\]]+\])+")]
    private static partial Regex QualifiedName();

    [GeneratedRegex(@"\[(@[A-Za-z0-9_@$#]+)\]")]
    private static partial Regex Parameter();

    [GeneratedRegex(@"'[^']*'")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\((\d{3,})\)")]
    private static partial Regex NumericLiteral();

    [GeneratedRegex(@"^(Expr|Bmk|Uniq|Union|PtnId|Segment|RaiseIfNull)\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex GeneratedName();

    [GeneratedRegex(@"\[([^\]]+)\]")]
    private static partial Regex BracketedIdentifier();
}
