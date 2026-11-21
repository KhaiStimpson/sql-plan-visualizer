using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlPlanViz.Editing.Completion;

/// <summary>The clause the caret is in. What a provider offers depends almost entirely on this.</summary>
public enum SqlClause
{
    Unknown,
    Select,
    From,
    Join,
    On,
    Where,
    GroupBy,
    OrderBy,
    Having,
    Insert,
    Into,
    Update,
    Set,
    Values,
    Declare,
    Exec,
}

/// <summary>A table the caret's statement can see, with the name the user will actually type.</summary>
public sealed class TableInScope
{
    public string Schema { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Alias { get; init; } = string.Empty;

    /// <summary>The alias if there is one, otherwise the bare table name.</summary>
    public string Qualifier => string.IsNullOrEmpty(Alias) ? Name : Alias;

    public string Display => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
}

/// <summary>
/// Everything a provider needs to know about where the caret is
/// (live-plan-editor-plan.md Phase 2).
///
/// Scope comes from the ScriptDom AST when the batch parses — the innermost
/// <c>QuerySpecification</c> containing the caret gives exactly the tables and aliases that
/// are visible there, including inside a subquery, which no amount of string matching gets
/// right. While a batch is mid-edit it usually does *not* parse, so a token-stream scan
/// recovers the same information approximately; the clause always comes from the token scan,
/// because the clause keyword the user just typed has nothing after it for the AST to bind.
/// </summary>
public sealed class CompletionContext
{
    public string Text { get; init; } = string.Empty;

    public int CaretOffset { get; init; }

    /// <summary>The partial word being completed. Empty on an explicit invoke in whitespace.</summary>
    public string Prefix { get; init; } = string.Empty;

    /// <summary>Document range a committed item replaces — the prefix, not just an insert point.</summary>
    public int ReplaceStart { get; init; }

    public int ReplaceLength { get; init; }

    public SqlClause Clause { get; init; } = SqlClause.Unknown;

    /// <summary>Text before the dot, when the caret follows one: <c>o.</c> → "o", <c>dbo.T.</c> → "dbo.T".</summary>
    public string Qualifier { get; init; } = string.Empty;

    public bool IsAfterDot => !string.IsNullOrEmpty(Qualifier);

    public IReadOnlyList<TableInScope> TablesInScope { get; init; } = [];

    /// <summary>Variables the batch declares before the caret, for completing <c>@…</c>.</summary>
    public IReadOnlyList<string> DeclaredVariables { get; init; } = [];

    /// <summary>True when the caret is inside a string or a comment, where nothing should fire.</summary>
    public bool IsInLiteralOrComment { get; init; }

    /// <summary>True for Ctrl+Space; type-ahead sets it false and is allowed to offer less.</summary>
    public bool ExplicitlyInvoked { get; init; }

    /// <summary>True when the prefix starts with '@' — only variables make sense.</summary>
    public bool IsVariableContext => Prefix.StartsWith('@');

    /// <summary>The table <see cref="Qualifier"/> names, if it resolves to one in scope.</summary>
    public TableInScope? QualifiedTable => string.IsNullOrEmpty(Qualifier)
        ? null
        : TablesInScope.FirstOrDefault(t =>
              string.Equals(t.Qualifier, Qualifier, StringComparison.OrdinalIgnoreCase))
          ?? TablesInScope.FirstOrDefault(t =>
              string.Equals(t.Display, Qualifier, StringComparison.OrdinalIgnoreCase));

    public static CompletionContext Create(
        SqlDocument document,
        int caretOffset,
        TSqlTokenizer? tokenizer = null,
        bool explicitInvoke = false,
        SqlParserVersion? parserVersion = null)
    {
        var text = document.Text;
        caretOffset = Math.Clamp(caretOffset, 0, text.Length);

        var (prefixStart, prefixLength) = PrefixAt(text, caretOffset);
        var prefix = text.Substring(prefixStart, prefixLength);
        var qualifier = QualifierBefore(text, prefixStart);

        var tokens = Lex(text, parserVersion);
        var clause = DetectClause(tokens, caretOffset);
        var tables = TablesFromAst(text, caretOffset, parserVersion);
        if (tables.Count == 0)
        {
            tables = TablesFromTokens(tokens, caretOffset);
        }

        return new CompletionContext
        {
            Text = text,
            CaretOffset = caretOffset,
            Prefix = prefix,
            ReplaceStart = prefixStart,
            ReplaceLength = prefixLength,
            Clause = clause,
            Qualifier = qualifier,
            TablesInScope = tables,
            DeclaredVariables = VariablesBefore(tokens, caretOffset),
            IsInLiteralOrComment = tokenizer?.IsInLiteralOrComment(caretOffset) ?? false,
            ExplicitlyInvoked = explicitInvoke,
        };
    }

    private static IReadOnlyList<TSqlParserToken> Lex(string text, SqlParserVersion? version)
    {
        try
        {
            using var reader = new StringReader(text);
            return [.. TSqlParserFactory.Create(version).GetTokenStream(reader, out _)];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>The identifier characters immediately before the caret. Includes a leading '@'.</summary>
    private static (int Start, int Length) PrefixAt(string text, int caret)
    {
        var start = caret;
        while (start > 0 && IsIdentifierChar(text[start - 1]))
        {
            start--;
        }

        // '@' and '#' only count when they lead the word, not in the middle of one.
        if (start > 0 && text[start - 1] is '@' or '#')
        {
            start--;
        }

        return (start, caret - start);
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    /// <summary>
    /// Reads the dotted qualifier immediately before <paramref name="prefixStart"/>, handling
    /// bracketed parts and two-part names: <c>[dbo].[Orders].</c> yields "dbo.Orders".
    /// </summary>
    private static string QualifierBefore(string text, int prefixStart)
    {
        var i = prefixStart;
        while (i > 0 && text[i - 1] is ' ' or '\t')
        {
            i--;
        }

        if (i == 0 || text[i - 1] != '.')
        {
            return string.Empty;
        }

        var parts = new List<string>();
        while (i > 0 && text[i - 1] == '.')
        {
            i--;
            while (i > 0 && text[i - 1] is ' ' or '\t')
            {
                i--;
            }

            if (i > 0 && text[i - 1] == ']')
            {
                var close = i - 1;
                var open = text.LastIndexOf('[', close);
                if (open < 0)
                {
                    break;
                }

                parts.Insert(0, text[(open + 1)..close]);
                i = open;
            }
            else
            {
                var end = i;
                while (i > 0 && IsIdentifierChar(text[i - 1]))
                {
                    i--;
                }

                if (i == end)
                {
                    break;
                }

                parts.Insert(0, text[i..end]);
            }

            while (i > 0 && text[i - 1] is ' ' or '\t')
            {
                i--;
            }
        }

        return string.Join('.', parts);
    }

    private static readonly Dictionary<TSqlTokenType, SqlClause> ClauseKeywords = new()
    {
        [TSqlTokenType.Select] = SqlClause.Select,
        [TSqlTokenType.From] = SqlClause.From,
        [TSqlTokenType.Join] = SqlClause.Join,
        [TSqlTokenType.On] = SqlClause.On,
        [TSqlTokenType.Where] = SqlClause.Where,
        [TSqlTokenType.Group] = SqlClause.GroupBy,
        [TSqlTokenType.Order] = SqlClause.OrderBy,
        [TSqlTokenType.Having] = SqlClause.Having,
        [TSqlTokenType.Insert] = SqlClause.Insert,
        [TSqlTokenType.Into] = SqlClause.Into,
        [TSqlTokenType.Update] = SqlClause.Update,
        [TSqlTokenType.Set] = SqlClause.Set,
        [TSqlTokenType.Values] = SqlClause.Values,
        [TSqlTokenType.Declare] = SqlClause.Declare,
        [TSqlTokenType.Exec] = SqlClause.Exec,
        [TSqlTokenType.Execute] = SqlClause.Exec,
        [TSqlTokenType.Delete] = SqlClause.From,
    };

    /// <summary>
    /// The nearest clause keyword before the caret, skipping over balanced parentheses so a
    /// finished <c>IN (SELECT …)</c> group does not claim the caret that follows it.
    /// </summary>
    private static SqlClause DetectClause(IReadOnlyList<TSqlParserToken> tokens, int caret)
    {
        var index = LastTokenBefore(tokens, caret);
        var depth = 0;

        for (var i = index; i >= 0; i--)
        {
            var type = tokens[i].TokenType;

            if (type == TSqlTokenType.RightParenthesis)
            {
                depth++;
                continue;
            }

            if (type == TSqlTokenType.LeftParenthesis)
            {
                // Below zero means this paren opened the group the caret sits inside — keep
                // walking, because the enclosing clause is still the answer.
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth > 0)
            {
                continue;
            }

            if (type is TSqlTokenType.Semicolon)
            {
                return SqlClause.Unknown;
            }

            if (ClauseKeywords.TryGetValue(type, out var clause))
            {
                return clause;
            }

            if (IsApply(tokens[i]))
            {
                return SqlClause.Join;
            }
        }

        return SqlClause.Unknown;
    }

    private static int LastTokenBefore(IReadOnlyList<TSqlParserToken> tokens, int caret)
    {
        var index = -1;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Offset >= caret)
            {
                break;
            }

            if (tokens[i].TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.EndOfFile))
            {
                index = i;
            }
        }

        return index;
    }

    private static List<string> VariablesBefore(IReadOnlyList<TSqlParserToken> tokens, int caret)
    {
        var names = new List<string>();
        foreach (var token in tokens)
        {
            if (token.Offset >= caret)
            {
                break;
            }

            if (token.TokenType == TSqlTokenType.Variable
                && token.Text is { Length: > 1 } text
                && !names.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(text);
            }
        }

        return names;
    }

    /// <summary>
    /// Tables visible at the caret, taken from the innermost query the AST says contains it.
    /// Returns empty when the batch does not parse, which is the caller's cue to fall back.
    /// </summary>
    private static List<TableInScope> TablesFromAst(string text, int caret, SqlParserVersion? version)
    {
        var fragment = TSqlParserFactory.TryParse(text, out _, version);
        if (fragment is null)
        {
            return [];
        }

        var visitor = new ScopeVisitor(caret);
        fragment.Accept(visitor);
        return visitor.Result;
    }

    private sealed class ScopeVisitor : TSqlFragmentVisitor
    {
        private readonly int _caret;
        private int _bestLength = int.MaxValue;

        public ScopeVisitor(int caret) => _caret = caret;

        public List<TableInScope> Result { get; } = [];

        public override void Visit(QuerySpecification node) => Consider(node, node.FromClause);

        public override void Visit(UpdateSpecification node) => Consider(node, node.FromClause);

        public override void Visit(DeleteSpecification node) => Consider(node, node.FromClause);

        private void Consider(TSqlFragment node, FromClause? from)
        {
            if (node.StartOffset > _caret || node.StartOffset + node.FragmentLength < _caret)
            {
                return;
            }

            // Innermost wins: a subquery's FROM shadows the outer one for a caret inside it.
            if (node.FragmentLength > _bestLength)
            {
                return;
            }

            _bestLength = node.FragmentLength;
            Result.Clear();
            if (from is not null)
            {
                foreach (var reference in from.TableReferences)
                {
                    Collect(reference, Result);
                }
            }
        }

        private static void Collect(TableReference reference, List<TableInScope> into)
        {
            switch (reference)
            {
                case NamedTableReference named:
                    into.Add(new TableInScope
                    {
                        Schema = named.SchemaObject?.SchemaIdentifier?.Value ?? string.Empty,
                        Name = named.SchemaObject?.BaseIdentifier?.Value ?? string.Empty,
                        Alias = named.Alias?.Value ?? string.Empty,
                    });
                    break;

                case QualifiedJoin join:
                    Collect(join.FirstTableReference, into);
                    Collect(join.SecondTableReference, into);
                    break;

                case UnqualifiedJoin unqualified:
                    Collect(unqualified.FirstTableReference, into);
                    Collect(unqualified.SecondTableReference, into);
                    break;

                case QueryDerivedTable derived when derived.Alias is not null:
                    into.Add(new TableInScope { Name = derived.Alias.Value, Alias = derived.Alias.Value });
                    break;

                case JoinParenthesisTableReference parenthesis:
                    Collect(parenthesis.Join, into);
                    break;

                case VariableTableReference variable when variable.Variable is not null:
                    into.Add(new TableInScope
                    {
                        Name = variable.Variable.Name,
                        Alias = variable.Alias?.Value ?? variable.Variable.Name,
                    });
                    break;
            }
        }
    }

    /// <summary>
    /// Approximate scope for a batch that does not parse — which is most of them, most of the
    /// time, because the user is halfway through typing one. Reads the name (and optional
    /// alias) following every FROM, JOIN and APPLY in the statement the caret belongs to.
    /// </summary>
    private static List<TableInScope> TablesFromTokens(IReadOnlyList<TSqlParserToken> tokens, int caret)
    {
        var result = new List<TableInScope>();
        var (scanFrom, scanTo) = ScopeRange(tokens, caret);

        for (var i = scanFrom; i <= scanTo && i < tokens.Count; i++)
        {
            var type = tokens[i].TokenType;
            if (type is not (TSqlTokenType.From or TSqlTokenType.Join) && !IsApply(tokens[i]))
            {
                continue;
            }

            var parts = new List<string>();
            var j = Next(tokens, i);
            while (j >= 0 && IsNamePart(tokens[j].TokenType))
            {
                parts.Add(Unbracket(tokens[j].Text ?? string.Empty));
                j = Next(tokens, j);
                if (j >= 0 && tokens[j].TokenType == TSqlTokenType.Dot)
                {
                    j = Next(tokens, j);
                    continue;
                }

                break;
            }

            if (parts.Count == 0)
            {
                continue;
            }

            var alias = string.Empty;
            if (j >= 0 && tokens[j].TokenType == TSqlTokenType.As)
            {
                j = Next(tokens, j);
            }

            if (j >= 0 && IsNamePart(tokens[j].TokenType))
            {
                alias = Unbracket(tokens[j].Text ?? string.Empty);
            }

            result.Add(new TableInScope
            {
                Schema = parts.Count > 1 ? parts[^2] : string.Empty,
                Name = parts[^1],
                Alias = alias,
            });
        }

        return result;
    }

    /// <summary>
    /// The token range the caret's scope covers. The AST does this properly; here it is
    /// approximated by taking the innermost parenthesised group around the caret that
    /// contains a FROM — a subquery — and falling back to the enclosing statement. Without
    /// it a caret inside a subquery would see the outer query's tables as well as its own.
    /// </summary>
    private static (int From, int To) ScopeRange(IReadOnlyList<TSqlParserToken> tokens, int caret)
    {
        var statementFrom = 0;
        var statementTo = tokens.Count - 1;

        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].TokenType != TSqlTokenType.Semicolon)
            {
                continue;
            }

            if (tokens[i].Offset < caret)
            {
                statementFrom = i + 1;
            }
            else
            {
                statementTo = i;
                break;
            }
        }

        // Every paren group that encloses the caret, innermost first.
        var open = new Stack<int>();
        var enclosing = new List<(int From, int To)>();
        for (var i = statementFrom; i <= statementTo && i < tokens.Count; i++)
        {
            switch (tokens[i].TokenType)
            {
                case TSqlTokenType.LeftParenthesis:
                    open.Push(i);
                    break;

                case TSqlTokenType.RightParenthesis when open.Count > 0:
                {
                    var start = open.Pop();
                    if (tokens[start].Offset < caret && tokens[i].Offset >= caret)
                    {
                        enclosing.Add((start + 1, i - 1));
                    }

                    break;
                }
            }
        }

        // Groups still open at the caret are enclosing too, and are the innermost of all.
        foreach (var start in open)
        {
            if (tokens[start].Offset < caret)
            {
                enclosing.Insert(0, (start + 1, statementTo));
            }
        }

        enclosing.Sort((a, b) => (a.To - a.From).CompareTo(b.To - b.From));

        foreach (var range in enclosing)
        {
            for (var i = range.From; i <= range.To && i < tokens.Count; i++)
            {
                if (tokens[i].TokenType == TSqlTokenType.From)
                {
                    return range;
                }
            }
        }

        return (statementFrom, statementTo);
    }

    /// <summary>APPLY is a contextual keyword, so ScriptDom lexes it as a plain identifier.</summary>
    private static bool IsApply(TSqlParserToken token) =>
        token.TokenType == TSqlTokenType.Identifier
        && string.Equals(token.Text, "APPLY", StringComparison.OrdinalIgnoreCase);

    private static bool IsNamePart(TSqlTokenType type) =>
        type is TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier or TSqlTokenType.Variable;

    private static int Next(IReadOnlyList<TSqlParserToken> tokens, int index)
    {
        for (var i = index + 1; i < tokens.Count; i++)
        {
            if (tokens[i].TokenType is not (TSqlTokenType.WhiteSpace
                or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment))
            {
                return tokens[i].TokenType == TSqlTokenType.EndOfFile ? -1 : i;
            }
        }

        return -1;
    }

    private static string Unbracket(string text) => text.Trim('[', ']', '"');
}
