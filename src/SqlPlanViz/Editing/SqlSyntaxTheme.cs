using Microsoft.UI;
using SqlPlanViz.Themes;
using Windows.UI;

namespace SqlPlanViz.Editing;

/// <summary>
/// Token colours for the SQL editor, shaped like <see cref="SqlPlanViz.Controls.PlanPalette"/>
/// and rebuilt on theme change for the same reason: Win2D draws outside the XAML resource
/// system, so the palette has to be mirrored in code.
///
/// Gauge has no categorical palette, so the token colours are Gauge roles: the accent for
/// structure (as on the canvas), the value roles for literals and code for parameters.
/// </summary>
public sealed class SqlSyntaxTheme
{
    public required bool IsDark { get; init; }

    public required Color Background { get; init; }

    public required Color Foreground { get; init; }

    public required Color Caret { get; init; }

    public required Color SelectionFill { get; init; }

    /// <summary>Fill behind the line the caret is on.</summary>
    public required Color CurrentLineFill { get; init; }

    /// <summary>Fill behind a bracket and its match, when the caret sits next to either.</summary>
    public required Color BracketMatchFill { get; init; }

    /// <summary>Fill behind every find-overlay match except the current one.</summary>
    public required Color FindMatchFill { get; init; }

    /// <summary>Fill behind the find overlay's current match — stronger than the others.</summary>
    public required Color FindMatchActiveFill { get; init; }

    public required Color GutterBackground { get; init; }

    public required Color LineNumber { get; init; }

    public required Color LineNumberActive { get; init; }

    public required Color Divider { get; init; }

    public required Color Keyword { get; init; }

    public required Color DataType { get; init; }

    public required Color Function { get; init; }

    public required Color Identifier { get; init; }

    public required Color QuotedIdentifier { get; init; }

    public required Color Variable { get; init; }

    public required Color StringLiteral { get; init; }

    public required Color NumberLiteral { get; init; }

    public required Color Comment { get; init; }

    public required Color Operator { get; init; }

    public required Color Punctuation { get; init; }

    /// <summary>Squiggle under a compile error (Phase 4).</summary>
    public required Color Error { get; init; }

    /// <summary>Gutter and annotation colours for the delta surfaces (Phase 5).</summary>
    public required Color Improved { get; init; }

    public required Color Regressed { get; init; }

    public required Color Added { get; init; }

    public required Color Annotation { get; init; }

    public Color For(SqlTokenClass tokenClass) => tokenClass switch
    {
        SqlTokenClass.Keyword => Keyword,
        SqlTokenClass.DataType => DataType,
        SqlTokenClass.Function => Function,
        SqlTokenClass.Identifier => Identifier,
        SqlTokenClass.QuotedIdentifier => QuotedIdentifier,
        SqlTokenClass.Variable => Variable,
        SqlTokenClass.StringLiteral => StringLiteral,
        SqlTokenClass.NumberLiteral => NumberLiteral,
        SqlTokenClass.Comment => Comment,
        SqlTokenClass.Operator => Operator,
        SqlTokenClass.Punctuation => Punctuation,
        SqlTokenClass.Label => Function,
        _ => Foreground,
    };

    /// <summary>The clause the selected operator maps to: an accent-dim wash with a 2px accent edge.</summary>
    public required Color ClauseFill { get; init; }

    public required Color ClauseEdge { get; init; }

    /// <summary>Bracket match: an outline in line-ctl rather than a fill.</summary>
    public required Color BracketMatchOutline { get; init; }

    /// <summary>
    /// Gauge roles (docs/gauge-design-system-plan.md Phase 6): keywords in the accent,
    /// identifiers fg-0, variables and parameters in code, numbers val-num, strings val-str,
    /// comments fg-3 (drawn italic), operators fg-1, on a bg-0 surface with fg-3 line numbers.
    /// Every value comes from <see cref="GaugeTokens"/>; never hand-copy hex here.
    /// </summary>
    /// <summary>Under a Windows contrast theme the editor takes the system colours (PLATFORMS.md).</summary>
    public static SqlSyntaxTheme Current(bool isDark)
    {
        if (!new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            return For(isDark);
        }

        var ui = new Windows.UI.ViewManagement.UISettings();
        var window = ui.UIElementColor(Windows.UI.ViewManagement.UIElementType.Window);
        var text = ui.UIElementColor(Windows.UI.ViewManagement.UIElementType.WindowText);
        var highlight = ui.UIElementColor(Windows.UI.ViewManagement.UIElementType.Highlight);
        var gray = ui.UIElementColor(Windows.UI.ViewManagement.UIElementType.GrayText);
        var link = ui.UIElementColor(Windows.UI.ViewManagement.UIElementType.Hotlight);
        return new SqlSyntaxTheme
        {
            IsDark = isDark,
            Background = window,
            Foreground = text,
            Caret = text,
            SelectionFill = highlight,
            CurrentLineFill = Colors.Transparent,
            BracketMatchFill = Colors.Transparent,
            BracketMatchOutline = text,
            FindMatchFill = highlight,
            FindMatchActiveFill = highlight,
            ClauseFill = window,
            ClauseEdge = highlight,
            GutterBackground = window,
            LineNumber = gray,
            LineNumberActive = text,
            Divider = window,
            Keyword = link,
            DataType = link,
            Function = link,
            Identifier = text,
            QuotedIdentifier = text,
            Variable = text,
            StringLiteral = text,
            NumberLiteral = text,
            Comment = gray,
            Operator = text,
            Punctuation = text,
            Error = text,
            Improved = text,
            Regressed = text,
            Added = text,
            Annotation = text,
        };
    }

    public static SqlSyntaxTheme For(bool isDark)
    {
        var t = isDark ? GaugeTokens.Dark : GaugeTokens.Light;
        return new SqlSyntaxTheme
        {
            IsDark = isDark,
            Background = t.Bg0,
            Foreground = t.Fg1,
            Caret = t.Fg0,
            SelectionFill = t.Sel,
            CurrentLineFill = Colors.Transparent,
            BracketMatchFill = Colors.Transparent,
            BracketMatchOutline = t.LineCtl,
            FindMatchFill = t.WarnDim,
            FindMatchActiveFill = t.AccentDim,
            ClauseFill = t.AccentDim,
            ClauseEdge = t.Accent,
            GutterBackground = t.Bg0,
            LineNumber = t.Fg3,
            LineNumberActive = t.Fg2,
            Divider = t.Bg0,
            Keyword = t.Accent,
            DataType = t.Accent,
            Function = t.Accent,
            Identifier = t.Fg0,
            QuotedIdentifier = t.Fg0,
            Variable = t.Code,
            StringLiteral = t.ValStr,
            NumberLiteral = t.ValNum,
            Comment = t.Fg3,
            Operator = t.Fg1,
            Punctuation = t.Fg1,
            Error = t.Crit,
            Improved = t.Ok,
            Regressed = t.Crit,
            Added = t.Warn,
            Annotation = t.Fg2,
        };
    }
}
