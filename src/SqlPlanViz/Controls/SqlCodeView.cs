using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using SqlPlanViz.Sql;
using Windows.Foundation;

namespace SqlPlanViz.Controls;

/// <summary>
/// The statement text, syntax-coloured, with the clause an operator maps to highlighted and
/// scrolled into view (tuning-roadmap.md Phase 5.7).
///
/// A <see cref="TextBox"/> cannot do this: its selection is invisible unless the box has focus,
/// and stealing focus from the canvas on every node click is not acceptable. So each line is its
/// own <see cref="TextBlock"/> of coloured <see cref="Run"/>s, the mapped span is painted with a
/// <see cref="TextHighlighter"/> (which does not need focus), and the line is scrolled to the
/// middle of the viewport. One element per line rather than per token keeps a long statement
/// cheap to build.
/// </summary>
public sealed class SqlCodeView : UserControl
{
    /// <summary>A statement longer than this is pathological; render the head and say so.</summary>
    private const int MaxRenderedLines = 4000;

    private const double GutterWidth = 44;

    private readonly ScrollViewer _scroll;
    private readonly StackPanel _lines;
    private readonly List<CodeLine> _rows = [];
    private readonly List<CodeLine> _highlighted = [];
    private readonly FontFamily _mono = new("Cascadia Mono, Consolas");
    private readonly SolidColorBrush _transparent = new(Colors.Transparent);
    private readonly Dictionary<SqlTokenKind, SolidColorBrush> _brushes = [];

    private SqlSyntaxPalette _palette = SqlSyntaxPalette.For(isDark: false);
    private SolidColorBrush _lineNumberBrush = new(Colors.Gray);
    private SolidColorBrush _spanBrush = new(Colors.Transparent);
    private SolidColorBrush _rowBrush = new(Colors.Transparent);
    private SolidColorBrush _accentBrush = new(Colors.Transparent);

    private string _source = string.Empty;
    private bool _formatted = true;
    private (int Start, int Length)? _highlight;

    public SqlCodeView()
    {
        _lines = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 4, 16, 8),
        };

        _scroll = new ScrollViewer
        {
            Content = _lines,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Enabled,
            ZoomMode = ZoomMode.Disabled,
        };

        Content = _scroll;
        ApplyPalette();
        ActualThemeChanged += (_, _) => Rebuild();
    }

    /// <summary>The text actually on screen — formatted or not — and the coordinate space every
    /// offset passed to <see cref="HighlightSpan"/> is measured in.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>Replaces the statement. Re-rendering is skipped when nothing changed, so
    /// clicking through operators does not rebuild the pane each time.</summary>
    public void SetSource(string? sql, bool formatted)
    {
        var source = sql ?? string.Empty;
        if (_source == source && _formatted == formatted)
        {
            return;
        }

        _source = source;
        _formatted = formatted;

        // Offsets from the previous text mean nothing in the new one.
        _highlight = null;
        Rebuild();
    }

    public void HighlightSpan(int start, int length)
    {
        _highlight = (start, Math.Max(0, length));
        ApplyHighlight(scrollIntoView: true);
    }

    public void ClearHighlight()
    {
        if (_highlight is null)
        {
            return;
        }

        _highlight = null;
        ApplyHighlight(scrollIntoView: false);
    }

    private void ApplyPalette()
    {
        _palette = SqlSyntaxPalette.For(ActualTheme == ElementTheme.Dark);
        _brushes.Clear();
        _lineNumberBrush = new SolidColorBrush(_palette.LineNumber);
        _spanBrush = new SolidColorBrush(_palette.SpanHighlight);
        _rowBrush = new SolidColorBrush(_palette.RowHighlight);
        _accentBrush = new SolidColorBrush(_palette.RowAccent);
    }

    private SolidColorBrush Brush(SqlTokenKind kind)
    {
        if (!_brushes.TryGetValue(kind, out var brush))
        {
            brush = new SolidColorBrush(_palette.ForKind(kind));
            _brushes[kind] = brush;
        }

        return brush;
    }

    private void Rebuild()
    {
        ApplyPalette();
        Text = _formatted ? SqlFormatter.Format(_source) : SqlFormatter.Normalize(_source).Trim();

        _lines.Children.Clear();
        _rows.Clear();
        _highlighted.Clear();

        var tokens = SqlTokenizer.Tokenize(Text);
        var tokenIndex = 0;
        var number = 0;
        var position = 0;

        while (true)
        {
            var newline = Text.IndexOf('\n', position);
            var length = (newline < 0 ? Text.Length : newline) - position;

            if (++number > MaxRenderedLines)
            {
                AddNotice("… the rest of this statement is not shown (it is unusually long).");
                break;
            }

            AddLine(number, position, length, tokens, ref tokenIndex);

            if (newline < 0)
            {
                break;
            }

            position = newline + 1;
        }

        ApplyHighlight(scrollIntoView: false);
    }

    private void AddLine(int number, int start, int length, IReadOnlyList<SqlToken> tokens, ref int tokenIndex)
    {
        var end = start + length;
        while (tokenIndex < tokens.Count && tokens[tokenIndex].End <= start)
        {
            tokenIndex++;
        }

        var code = new TextBlock
        {
            FontFamily = _mono,
            FontSize = 12,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = Brush(SqlTokenKind.Identifier),
        };

        // A token can straddle the line break (a block comment, an indent run), so clip rather
        // than assume tokens and lines line up.
        for (var probe = tokenIndex; probe < tokens.Count && tokens[probe].Start < end; probe++)
        {
            var token = tokens[probe];
            var from = Math.Max(token.Start, start);
            var to = Math.Min(token.End, end);
            if (to > from)
            {
                code.Inlines.Add(new Run { Text = Text[from..to], Foreground = Brush(token.Kind) });
            }
        }

        if (code.Inlines.Count == 0)
        {
            // An empty TextBlock has no height, which would collapse blank lines out of the view.
            code.Inlines.Add(new Run { Text = " " });
        }

        var accent = new Border { Width = 3, Background = _transparent };

        var row = new Grid { Background = _transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GutterWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var gutter = new TextBlock
        {
            FontFamily = _mono,
            FontSize = 12,
            Foreground = _lineNumberBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 10, 0),
            Text = number.ToString(),
        };

        Grid.SetColumn(gutter, 1);
        Grid.SetColumn(code, 2);
        row.Children.Add(accent);
        row.Children.Add(gutter);
        row.Children.Add(code);

        _lines.Children.Add(row);
        _rows.Add(new CodeLine(start, length, row, accent, code));
    }

    private void AddNotice(string message) => _lines.Children.Add(new TextBlock
    {
        FontSize = 11,
        FontStyle = Windows.UI.Text.FontStyle.Italic,
        Foreground = _lineNumberBrush,
        Margin = new Thickness(GutterWidth + 3, 6, 0, 0),
        Text = message,
    });

    private void ApplyHighlight(bool scrollIntoView)
    {
        // Only the previously marked lines are cleared: touching every TextBlock in a long
        // statement on every operator click is the one thing that would make this pane feel slow.
        foreach (var line in _highlighted)
        {
            line.Text.TextHighlighters.Clear();
            line.Row.Background = _transparent;
            line.Accent.Background = _transparent;
        }

        _highlighted.Clear();

        if (_highlight is not { } span)
        {
            return;
        }

        var (spanStart, spanLength) = span;
        var spanEnd = spanStart + spanLength;
        FrameworkElement? target = null;

        foreach (var line in _rows)
        {
            var lineEnd = line.Start + line.Length;

            // A blank line inside the span still belongs to it, but has no characters to paint.
            var touches = line.Length == 0
                ? spanStart <= line.Start && spanEnd > line.Start
                : spanStart < lineEnd && spanEnd > line.Start;
            if (!touches)
            {
                continue;
            }

            var from = Math.Max(0, spanStart - line.Start);
            var to = Math.Min(line.Length, spanEnd - line.Start);
            if (to > from)
            {
                var highlighter = new TextHighlighter { Background = _spanBrush };
                highlighter.Ranges.Add(new TextRange { StartIndex = from, Length = to - from });
                line.Text.TextHighlighters.Add(highlighter);
            }

            line.Row.Background = _rowBrush;
            line.Accent.Background = _accentBrush;
            _highlighted.Add(line);
            target ??= line.Row;
        }

        if (scrollIntoView && target is not null)
        {
            ScrollTo(target);
        }
    }

    private void ScrollTo(FrameworkElement row)
    {
        UpdateLayout();
        if (TryScrollTo(row))
        {
            return;
        }

        // Nothing is measured yet on the first plan load; try again once layout has run.
        DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, () => TryScrollTo(row));
    }

    private bool TryScrollTo(FrameworkElement row)
    {
        if (_scroll.ViewportHeight <= 0 || row.ActualHeight <= 0)
        {
            return false;
        }

        var offset = row.TransformToVisual(_scroll).TransformPoint(new Point(0, 0)).Y + _scroll.VerticalOffset;
        var centred = offset - Math.Max(0, (_scroll.ViewportHeight - row.ActualHeight) / 2);
        return _scroll.ChangeView(0, Math.Max(0, centred), null);
    }

    private sealed record CodeLine(int Start, int Length, Grid Row, Border Accent, TextBlock Text);
}
