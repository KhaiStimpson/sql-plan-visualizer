using SqlPlanViz.Sql;
using Windows.UI;

namespace SqlPlanViz.Controls;

/// <summary>
/// Theme-aware colours for the SQL pane. Kept beside <see cref="PlanPalette"/> and in the same
/// shape, so the two halves of the window (canvas and SQL) agree on what "selected" looks like.
/// </summary>
public sealed class SqlSyntaxPalette
{
    public required Color Keyword { get; init; }

    public required Color Function { get; init; }

    public required Color String { get; init; }

    public required Color Number { get; init; }

    public required Color Comment { get; init; }

    public required Color Identifier { get; init; }

    public required Color Variable { get; init; }

    public required Color Operator { get; init; }

    public required Color Punctuation { get; init; }

    public required Color LineNumber { get; init; }

    /// <summary>Fill behind the exact characters the operator maps to.</summary>
    public required Color SpanHighlight { get; init; }

    /// <summary>A wash across the whole line, so the match is findable while scrolling.</summary>
    public required Color RowHighlight { get; init; }

    /// <summary>The strip in the gutter — the same cue the canvas uses for a selected node.</summary>
    public required Color RowAccent { get; init; }

    public static SqlSyntaxPalette For(bool isDark) => isDark
        ? new SqlSyntaxPalette
        {
            Keyword = Rgb(0x7C, 0xB7, 0xF7),
            Function = Rgb(0xDC, 0xDC, 0xAA),
            String = Rgb(0xE0, 0x9B, 0x74),
            Number = Rgb(0xB5, 0xCE, 0xA8),
            Comment = Rgb(0x79, 0x9E, 0x6E),
            Identifier = Rgb(0xE4, 0xE6, 0xEA),
            Variable = Rgb(0x9C, 0xDC, 0xFE),
            Operator = Rgb(0xC8, 0xCD, 0xD4),
            Punctuation = Rgb(0xA8, 0xAE, 0xB8),
            LineNumber = Rgb(0x6E, 0x76, 0x81),
            SpanHighlight = Argb(0x66, 0x60, 0xB0, 0xFF),
            RowHighlight = Argb(0x1F, 0x60, 0xB0, 0xFF),
            RowAccent = Rgb(0x60, 0xB0, 0xFF),
        }
        : new SqlSyntaxPalette
        {
            Keyword = Rgb(0x0B, 0x54, 0xB5),
            Function = Rgb(0x6C, 0x3A, 0x8C),
            String = Rgb(0xA3, 0x15, 0x15),
            Number = Rgb(0x0A, 0x70, 0x45),
            Comment = Rgb(0x4A, 0x7A, 0x4A),
            Identifier = Rgb(0x1B, 0x1E, 0x23),
            Variable = Rgb(0x0F, 0x5C, 0x9E),
            Operator = Rgb(0x44, 0x4A, 0x52),
            Punctuation = Rgb(0x5A, 0x63, 0x70),
            LineNumber = Rgb(0x9A, 0xA0, 0xA6),
            SpanHighlight = Argb(0x40, 0x00, 0x67, 0xC0),
            RowHighlight = Argb(0x14, 0x00, 0x67, 0xC0),
            RowAccent = Rgb(0x00, 0x67, 0xC0),
        };

    public Color ForKind(SqlTokenKind kind) => kind switch
    {
        SqlTokenKind.Keyword => Keyword,
        SqlTokenKind.Function => Function,
        SqlTokenKind.String => String,
        SqlTokenKind.Number => Number,
        SqlTokenKind.Comment => Comment,
        SqlTokenKind.Variable => Variable,
        SqlTokenKind.Operator => Operator,
        SqlTokenKind.Punctuation => Punctuation,
        _ => Identifier,
    };

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    private static Color Argb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);
}
