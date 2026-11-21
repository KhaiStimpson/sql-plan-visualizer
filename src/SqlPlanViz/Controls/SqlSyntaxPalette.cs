using SqlPlanViz.Sql;
using SqlPlanViz.Themes;
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

    /// <summary>The same Gauge roles as <see cref="Editing.SqlSyntaxTheme"/>, from <see cref="GaugeTokens"/>.</summary>
    public static SqlSyntaxPalette For(bool isDark)
    {
        var t = isDark ? GaugeTokens.Dark : GaugeTokens.Light;
        return new SqlSyntaxPalette
        {
            Keyword = t.Accent,
            Function = t.Accent,
            String = t.ValStr,
            Number = t.ValNum,
            Comment = t.Fg3,
            Identifier = t.Fg0,
            Variable = t.Code,
            Operator = t.Fg1,
            Punctuation = t.Fg1,
            LineNumber = t.Fg3,
            SpanHighlight = t.AccentDim,
            RowHighlight = t.AccentDim,
            RowAccent = t.Accent,
        };
    }

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

}
