using Windows.UI;

namespace SqlPlanViz.Editing;

/// <summary>
/// Token colours for the SQL editor, shaped like <see cref="SqlPlanViz.Controls.PlanPalette"/>
/// and rebuilt on theme change for the same reason: Win2D draws outside the XAML resource
/// system, so the palette has to be mirrored in code.
///
/// The hues deliberately echo the plan canvas — the same blue means "structure" in both
/// panes — rather than importing a third-party editor theme wholesale.
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

    public static SqlSyntaxTheme For(bool isDark) => isDark
        ? new SqlSyntaxTheme
        {
            IsDark = true,
            Background = Rgb(0x1A, 0x1A, 0x1E),
            Foreground = Rgb(0xE6, 0xE8, 0xEC),
            Caret = Rgb(0xF2, 0xF3, 0xF5),
            SelectionFill = Argb(0x55, 0x3C, 0x6E, 0xA8),
            CurrentLineFill = Argb(0x30, 0x3A, 0x3F, 0x4A),
            GutterBackground = Rgb(0x17, 0x18, 0x1B),
            LineNumber = Rgb(0x5F, 0x67, 0x72),
            LineNumberActive = Rgb(0xB6, 0xBE, 0xC9),
            Divider = Rgb(0x2E, 0x32, 0x39),
            Keyword = Rgb(0x6C, 0xB6, 0xFF),
            DataType = Rgb(0x5F, 0xD0, 0xB0),
            Function = Rgb(0xD9, 0xB2, 0xFF),
            Identifier = Rgb(0xE6, 0xE8, 0xEC),
            QuotedIdentifier = Rgb(0x9C, 0xDC, 0xC0),
            Variable = Rgb(0xFF, 0xC1, 0x4E),
            StringLiteral = Rgb(0xE8, 0x9A, 0x74),
            NumberLiteral = Rgb(0xB5, 0xCE, 0xA8),
            Comment = Rgb(0x6E, 0x7A, 0x88),
            Operator = Rgb(0xC0, 0xC8, 0xD2),
            Punctuation = Rgb(0x8C, 0x95, 0xA1),
            Error = Rgb(0xFF, 0x6B, 0x63),
            Improved = Rgb(0x5A, 0xC8, 0x8A),
            Regressed = Rgb(0xFF, 0x6B, 0x63),
            Added = Rgb(0x6C, 0xB6, 0xFF),
            Annotation = Rgb(0x7C, 0x83, 0x8E),
        }
        : new SqlSyntaxTheme
        {
            IsDark = false,
            Background = Rgb(0xFD, 0xFD, 0xFE),
            Foreground = Rgb(0x1B, 0x1E, 0x23),
            Caret = Rgb(0x11, 0x13, 0x17),
            SelectionFill = Argb(0x44, 0x3C, 0x84, 0xD8),
            CurrentLineFill = Argb(0x22, 0xC4, 0xCC, 0xD6),
            GutterBackground = Rgb(0xF4, 0xF6, 0xF8),
            LineNumber = Rgb(0x9A, 0xA4, 0xB2),
            LineNumberActive = Rgb(0x4A, 0x53, 0x60),
            Divider = Rgb(0xE1, 0xE5, 0xEA),
            Keyword = Rgb(0x00, 0x55, 0xB8),
            DataType = Rgb(0x0A, 0x7A, 0x6B),
            Function = Rgb(0x79, 0x3B, 0xB0),
            Identifier = Rgb(0x1B, 0x1E, 0x23),
            QuotedIdentifier = Rgb(0x0F, 0x6B, 0x5C),
            Variable = Rgb(0x9A, 0x5B, 0x00),
            StringLiteral = Rgb(0xA3, 0x31, 0x14),
            NumberLiteral = Rgb(0x1F, 0x6F, 0x3C),
            Comment = Rgb(0x6B, 0x76, 0x84),
            Operator = Rgb(0x3A, 0x42, 0x4E),
            Punctuation = Rgb(0x70, 0x79, 0x86),
            Error = Rgb(0xC4, 0x2B, 0x1C),
            Improved = Rgb(0x1B, 0x82, 0x4E),
            Regressed = Rgb(0xC4, 0x2B, 0x1C),
            Added = Rgb(0x00, 0x67, 0xC0),
            Annotation = Rgb(0x8A, 0x93, 0xA0),
        };

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    private static Color Argb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);
}
