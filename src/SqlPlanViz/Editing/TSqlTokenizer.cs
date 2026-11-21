using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlPlanViz.Editing;

/// <summary>What a span of text is, for colouring. Deliberately coarser than ScriptDom's 242 token types.</summary>
public enum SqlTokenClass
{
    Plain,
    Keyword,
    DataType,
    Function,
    Identifier,
    QuotedIdentifier,
    Variable,
    StringLiteral,
    NumberLiteral,
    Comment,
    Operator,
    Punctuation,
    Label,
}

/// <summary>A classified run of characters, with offsets relative to the start of its line.</summary>
public readonly record struct ClassifiedSpan(int Start, int Length, SqlTokenClass Class)
{
    public int End => Start + Length;
}

/// <summary>
/// Syntax classification over <see cref="SqlDocument"/>, backed by ScriptDom's lexer
/// (live-plan-editor-plan.md Phase 1).
///
/// Results are cached per line with line-relative offsets, which is what makes the
/// re-tokenize incremental in the way the plan asks for: an edit re-lexes from the nearest
/// line that is not inside a block comment or a multi-line string down to the end of the
/// edited region, and the untouched tail below keeps its cached spans without so much as an
/// offset fix-up.
///
/// A batch that does not parse still lexes — highlighting degrades gracefully rather than
/// disappearing while you are half way through typing a word.
/// </summary>
public sealed class TSqlTokenizer
{
    /// <summary>Lines re-lexed past the edit before giving up and going to the end of the document.</summary>
    private const int OverscanLines = 64;

    private sealed class LineTokens
    {
        public List<ClassifiedSpan> Spans { get; init; } = [];

        /// <summary>True when a block comment or multi-line string was still open at the line's end.</summary>
        public bool OpenAtEnd { get; set; }
    }

    private readonly SqlDocument _document;
    private readonly List<LineTokens> _lines = [];

    public TSqlTokenizer(SqlDocument document, SqlParserVersion? version = null)
    {
        _document = document;
        Version = version;
        _document.Changed += OnDocumentChanged;
        TokenizeAll();
    }

    /// <summary>Null follows <see cref="TSqlParserFactory.Default"/>, which tracks the connected server.</summary>
    public SqlParserVersion? Version { get; set; }

    /// <summary>Lexer diagnostics for the whole document. Empty is the common case even for invalid SQL.</summary>
    public IReadOnlyList<ParseError> LexErrors { get; private set; } = [];

    /// <summary>Raised after a re-tokenize, so the control can invalidate.</summary>
    public event EventHandler? Retokenized;

    /// <summary>Classified spans for one line, offsets relative to that line's start.</summary>
    public IReadOnlyList<ClassifiedSpan> SpansForLine(int line) =>
        line >= 0 && line < _lines.Count ? _lines[line].Spans : [];

    /// <summary>The classification at an absolute document offset, for hover and completion context.</summary>
    public SqlTokenClass ClassAt(int offset)
    {
        var line = _document.LineOf(offset);
        var relative = offset - _document.GetLineStart(line);
        foreach (var span in SpansForLine(line))
        {
            if (relative >= span.Start && relative < span.End)
            {
                return span.Class;
            }
        }

        return SqlTokenClass.Plain;
    }

    /// <summary>True when the offset sits inside a comment or a string — where completions must not fire.</summary>
    public bool IsInLiteralOrComment(int offset)
    {
        // A caret sitting immediately after the opening quote is inside the string even though
        // the character under it may belong to the next token, so check the character before too.
        var here = ClassAt(offset);
        var before = offset > 0 ? ClassAt(offset - 1) : SqlTokenClass.Plain;
        return here is SqlTokenClass.Comment or SqlTokenClass.StringLiteral
               || before is SqlTokenClass.Comment or SqlTokenClass.StringLiteral;
    }

    public void Detach() => _document.Changed -= OnDocumentChanged;

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (e.IsWholeDocument || _lines.Count == 0)
        {
            TokenizeAll();
        }
        else
        {
            TokenizeIncremental(e);
        }

        Retokenized?.Invoke(this, EventArgs.Empty);
    }

    private void TokenizeAll()
    {
        _lines.Clear();
        var produced = Lex(0, _document.LineCount - 1, out var errors);
        _lines.AddRange(produced);
        LexErrors = errors;
    }

    private void TokenizeIncremental(DocumentChangedEventArgs e)
    {
        // A lexical error — an unterminated string or block comment — makes ScriptDom stop
        // emitting tokens at the error, so the token stream below it is not a function of the
        // text below it any more. That breaks the splice in both directions: while the error
        // stands, and again on the keystroke that closes the quote. Re-lexing the whole
        // document is the only correct answer, and it only costs anything while the batch is
        // mid-edit and invalid.
        if (LexErrors.Count > 0)
        {
            TokenizeAll();
            return;
        }

        // Back up to a line that is known not to start inside a block comment or string.
        // Everything above it is unaffected by an edit below, by definition.
        var anchor = Math.Clamp(e.StartLine, 0, _lines.Count - 1);
        while (anchor > 0 && _lines[anchor - 1].OpenAtEnd)
        {
            anchor--;
        }

        var lineDelta = e.EndLineAfter - e.EndLineBefore;
        var lastLine = _document.LineCount - 1;
        var end = Math.Clamp(e.EndLineAfter, anchor, lastLine);

        while (true)
        {
            var produced = Lex(anchor, end, out var errors);
            if (errors.Count > 0)
            {
                TokenizeAll();
                return;
            }

            // Re-lexing can change whether the last line leaves a construct open; if it does,
            // every line below it is classified differently and has to be redone as well.
            var oldIndex = end - lineDelta;
            var stateMatches = end == lastLine
                               || (oldIndex >= 0 && oldIndex < _lines.Count
                                   && _lines[oldIndex].OpenAtEnd == produced[^1].OpenAtEnd);

            if (stateMatches || end >= lastLine)
            {
                var removeFrom = anchor;
                var removeCount = Math.Min(end - lineDelta - anchor + 1, _lines.Count - anchor);
                if (removeCount > 0)
                {
                    _lines.RemoveRange(removeFrom, removeCount);
                }

                _lines.InsertRange(removeFrom, produced);

                // Guard against index drift from a malformed change record: the cache must
                // always describe exactly the document's lines.
                if (_lines.Count != _document.LineCount)
                {
                    TokenizeAll();
                    return;
                }

                LexErrors = errors;
                return;
            }

            end = Math.Min(end + OverscanLines, lastLine);
        }
    }

    /// <summary>Lexes the inclusive line range and returns one entry per line in it.</summary>
    private List<LineTokens> Lex(int firstLine, int lastLine, out IReadOnlyList<ParseError> errors)
    {
        firstLine = Math.Clamp(firstLine, 0, _document.LineCount - 1);
        lastLine = Math.Clamp(lastLine, firstLine, _document.LineCount - 1);

        var regionStart = _document.GetLineStart(firstLine);
        var regionEnd = lastLine + 1 < _document.LineCount
            ? _document.GetLineStart(lastLine + 1)
            : _document.Length;

        var result = new List<LineTokens>(lastLine - firstLine + 1);
        for (var i = firstLine; i <= lastLine; i++)
        {
            result.Add(new LineTokens());
        }

        var text = _document.GetText(regionStart, regionEnd - regionStart);
        IList<ParseError> lexErrors = [];
        IList<TSqlParserToken> tokens;
        try
        {
            using var reader = new StringReader(text);
            tokens = TSqlParserFactory.Create(Version).GetTokenStream(reader, out lexErrors);
        }
        catch (Exception)
        {
            // Highlighting is cosmetic; never let it break typing.
            errors = [];
            return result;
        }

        foreach (var token in tokens)
        {
            if (token.TokenType is TSqlTokenType.EndOfFile or TSqlTokenType.WhiteSpace)
            {
                continue;
            }

            var cls = Classify(token);
            if (cls == SqlTokenClass.Plain)
            {
                continue;
            }

            var absoluteStart = regionStart + token.Offset;
            var length = token.Text?.Length ?? 0;
            if (length <= 0)
            {
                continue;
            }

            var unterminated = IsUnterminated(token);

            // A block comment or a multi-line string is one token spanning several lines; split
            // it so each line owns the piece of it that it draws.
            var startLine = _document.LineOf(absoluteStart);
            var endLine = _document.LineOf(Math.Min(absoluteStart + length, _document.Length));
            for (var line = startLine; line <= endLine && line <= lastLine; line++)
            {
                if (line < firstLine)
                {
                    continue;
                }

                var lineStart = _document.GetLineStart(line);
                var lineEnd = _document.GetLineEnd(line);
                var pieceStart = Math.Max(absoluteStart, lineStart);
                var pieceEnd = Math.Min(absoluteStart + length, lineEnd);

                // The line ends inside this token either because the token continues onto the
                // next line, or because the token never closes — an unterminated block comment
                // at the end of the re-lexed region leaves every line below it in comment
                // state, and missing that is what makes an incremental result diverge from a
                // full one. Recorded before the empty-piece check below, because a blank line
                // in the middle of a block comment draws nothing but is still inside it.
                if (line < endLine || (unterminated && pieceEnd >= regionEnd))
                {
                    result[line - firstLine].OpenAtEnd = true;
                }

                if (pieceEnd <= pieceStart)
                {
                    continue;
                }

                result[line - firstLine].Spans.Add(
                    new ClassifiedSpan(pieceStart - lineStart, pieceEnd - pieceStart, cls));
            }
        }

        errors = [.. lexErrors];
        return result;
    }

    /// <summary>
    /// True for a token that opens a multi-line construct and never closes it. Only these
    /// token types can carry state across a line break, so only these need checking.
    /// </summary>
    private static bool IsUnterminated(TSqlParserToken token)
    {
        var text = token.Text ?? string.Empty;

        return token.TokenType switch
        {
            TSqlTokenType.MultilineComment => text.Length < 4 || !text.EndsWith("*/", StringComparison.Ordinal),
            TSqlTokenType.AsciiStringLiteral => !EndsWithQuote(text, '\''),
            TSqlTokenType.UnicodeStringLiteral => !EndsWithQuote(text, '\''),
            TSqlTokenType.AsciiStringOrQuotedIdentifier => !EndsWithQuote(text, '\'') && !EndsWithQuote(text, '"'),
            TSqlTokenType.QuotedIdentifier => !EndsWithQuote(text, ']') && !EndsWithQuote(text, '"'),
            _ => false,
        };
    }

    private static bool EndsWithQuote(string text, char quote)
    {
        // "N'" and "'" are openers, not one-character closed literals.
        var opener = text.IndexOf(quote);
        return opener >= 0 && text.Length > opener + 1 && text[^1] == quote;
    }

    /// <summary>
    /// ScriptDom lays its token types out in blocks: every reserved word falls between
    /// <c>Add</c> and <c>TryConvert</c>, and every operator or punctuation mark between
    /// <c>Bang</c> and <c>ConcatEquals</c>. Naming the boundaries beats writing out 180 cases.
    /// </summary>
    private static SqlTokenClass Classify(TSqlParserToken token)
    {
        var type = token.TokenType;

        switch (type)
        {
            case TSqlTokenType.SingleLineComment:
            case TSqlTokenType.MultilineComment:
                return SqlTokenClass.Comment;

            case TSqlTokenType.AsciiStringLiteral:
            case TSqlTokenType.UnicodeStringLiteral:
            case TSqlTokenType.AsciiStringOrQuotedIdentifier:
                return SqlTokenClass.StringLiteral;

            case TSqlTokenType.Integer:
            case TSqlTokenType.Numeric:
            case TSqlTokenType.Real:
            case TSqlTokenType.Money:
            case TSqlTokenType.HexLiteral:
                return SqlTokenClass.NumberLiteral;

            case TSqlTokenType.Variable:
            case TSqlTokenType.SqlCommandIdentifier:
                return SqlTokenClass.Variable;

            case TSqlTokenType.QuotedIdentifier:
                return SqlTokenClass.QuotedIdentifier;

            case TSqlTokenType.Label:
                return SqlTokenClass.Label;

            case TSqlTokenType.Go:
                return SqlTokenClass.Keyword;

            case TSqlTokenType.Identifier:
            case TSqlTokenType.ProcNameSemicolon:
            case TSqlTokenType.PseudoColumn:
            case TSqlTokenType.DollarPartition:
            {
                var text = token.Text ?? string.Empty;
                if (SqlLanguage.IsDataType(text)) return SqlTokenClass.DataType;
                if (SqlLanguage.IsFunction(text)) return SqlTokenClass.Function;
                return SqlTokenClass.Identifier;
            }
        }

        if (type >= TSqlTokenType.Add && type <= TSqlTokenType.TryConvert)
        {
            return SqlTokenClass.Keyword;
        }

        if (type >= TSqlTokenType.Bang && type <= TSqlTokenType.ConcatEquals)
        {
            return type is TSqlTokenType.Comma or TSqlTokenType.Semicolon or TSqlTokenType.Dot
                or TSqlTokenType.LeftParenthesis or TSqlTokenType.RightParenthesis
                or TSqlTokenType.LeftCurly or TSqlTokenType.RightCurly
                ? SqlTokenClass.Punctuation
                : SqlTokenClass.Operator;
        }

        return SqlTokenClass.Plain;
    }
}
