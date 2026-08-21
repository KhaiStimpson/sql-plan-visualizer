namespace SqlPlanViz.Editing.Completion;

/// <summary>What a completion item is, which drives its glyph and its default rank.</summary>
public enum CompletionItemKind
{
    Keyword,
    Snippet,
    DataType,
    Function,
    Schema,
    Table,
    View,
    TableType,
    Column,
    Index,
    Alias,
    Variable,

    /// <summary>Drawn from the diagnostics layer — a fix, not just a name (Phase 6).</summary>
    TuningSuggestion,
}

/// <summary>How well an item matched what the user had typed. Ordered best first.</summary>
public enum CompletionMatchKind
{
    Exact,
    Prefix,

    /// <summary>Matched the initials of a multi-part name, e.g. "oli" → OrderLineItems.</summary>
    Initials,
    Substring,
    Subsequence,
    None,
}

public sealed class CompletionItem
{
    public string Label { get; init; } = string.Empty;

    /// <summary>What actually goes into the document. Defaults to the label.</summary>
    public string InsertText
    {
        get => _insertText ?? Label;
        init => _insertText = value;
    }

    private readonly string? _insertText;

    public CompletionItemKind Kind { get; init; }

    /// <summary>Right-hand grey text: a data type, an owning table, an index's columns.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Longer explanation, shown under the list when the item is highlighted.</summary>
    public string Documentation { get; init; } = string.Empty;

    /// <summary>
    /// Tie-breaker within a match quality, lower first. Providers set it to express "this is
    /// the obvious answer here" — a column of a table already in scope beats a keyword.
    /// </summary>
    public int SortRank { get; init; } = 100;

    /// <summary>Marks the item visually as a suggestion rather than a name (Phase 6).</summary>
    public bool IsSuggestion { get; init; }

    /// <summary>Which provider produced this, so a provider can be disabled without re-querying.</summary>
    public string ProviderId { get; init; } = string.Empty;

    /// <summary>Set by the engine while filtering; not part of the provider's output.</summary>
    public CompletionMatchKind MatchKind { get; set; } = CompletionMatchKind.None;

    /// <summary>Short glyph for the list. Segoe Fluent Icons would need a font check, so text it is.</summary>
    public string KindGlyph => Kind switch
    {
        CompletionItemKind.Keyword => "K",
        CompletionItemKind.Snippet => "{}",
        CompletionItemKind.DataType => "T",
        CompletionItemKind.Function => "ƒ",
        CompletionItemKind.Schema => "S",
        CompletionItemKind.Table => "▦",
        CompletionItemKind.View => "▤",
        CompletionItemKind.TableType => "▩",
        CompletionItemKind.Column => "•",
        CompletionItemKind.Index => "⌗",
        CompletionItemKind.Alias => "α",
        CompletionItemKind.Variable => "@",
        CompletionItemKind.TuningSuggestion => "★",
        _ => "·",
    };
}
