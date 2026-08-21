using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using SqlPlanViz.Editing;
using Windows.Foundation;
using Windows.UI;

namespace SqlPlanViz.Controls;

/// <summary>
/// A native T-SQL editor drawn immediate-mode on Win2D (live-plan-editor-plan.md Phase 1).
///
/// The plan rules out WebView2 and Monaco, so this is a real editor built on the same
/// primitives as <see cref="PlanCanvas"/>: one <see cref="CanvasControl"/>, per-frame drawing,
/// and viewport virtualization so only the visible lines are laid out and painted. Text
/// itself lives in <see cref="SqlDocument"/> and classification in <see cref="TSqlTokenizer"/>;
/// neither knows this control exists, which is what makes them testable off Windows.
///
/// Input arrives through <see cref="Windows.UI.Text.Core.CoreTextEditContext"/> where the
/// system provides one, so IME composition, dead keys and the touch keyboard behave — see
/// <c>SqlEditorControl.Input.cs</c>.
/// </summary>
public sealed partial class SqlEditorControl : UserControl
{
    private const float GutterMarkWidth = 14f;
    private const float LineNumberPadding = 10f;
    private const float TextLeftPadding = 8f;
    /// <summary>Lines drawn beyond the viewport, so a fast scroll never shows a blank band.</summary>
    private const int OverdrawLines = 2;

    private readonly CanvasControl _canvas = new();
    private readonly ScrollBar _verticalScroll = new()
    {
        Orientation = Orientation.Vertical,
        IndicatorMode = ScrollingIndicatorMode.MouseIndicator,
    };

    private readonly ScrollBar _horizontalScroll = new()
    {
        Orientation = Orientation.Horizontal,
        IndicatorMode = ScrollingIndicatorMode.MouseIndicator,
    };

    private readonly DispatcherTimer _caretTimer = new() { Interval = TimeSpan.FromMilliseconds(530) };

    /// <summary>Text layouts for lines currently on screen. Rebuilt only for lines that changed.</summary>
    private readonly Dictionary<int, CanvasTextLayout> _lineLayouts = [];

    private SqlDocument _document = new();
    private TSqlTokenizer _tokenizer;
    private SqlSyntaxTheme _theme = SqlSyntaxTheme.For(isDark: false);

    private CanvasTextFormat _textFormat = null!;
    private CanvasTextFormat _lineNumberFormat = null!;
    private CanvasTextFormat _annotationFormat = null!;

    private float _lineHeight = 17f;
    private float _charWidth = 8f;
    private float _gutterWidth = 56f;

    private int _caret;
    private int _anchor;

    /// <summary>Column the caret "wants" while moving vertically, so up/down across short lines is stable.</summary>
    private int _desiredColumn = -1;

    private double _scrollX;
    private double _scrollY;

    private bool _caretVisible = true;
    private bool _hasFocus;

    public SqlEditorControl()
    {
        _tokenizer = new TSqlTokenizer(_document);
        _tokenizer.Retokenized += (_, _) => InvalidateLineCache();
        _document.Changed += OnDocumentChanged;

        CreateTextFormats();

        _canvas.ClearColor = Colors.Transparent;
        _canvas.CreateResources += OnCreateResources;
        _canvas.Draw += OnDraw;
        _canvas.SizeChanged += (_, _) => UpdateScrollRanges();

        _verticalScroll.Scroll += (_, e) => SetScroll(_scrollX, e.NewValue);
        _horizontalScroll.Scroll += (_, e) => SetScroll(e.NewValue, _scrollY);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetColumn(_canvas, 0);
        Grid.SetRow(_canvas, 0);
        Grid.SetColumn(_verticalScroll, 1);
        Grid.SetRow(_verticalScroll, 0);
        Grid.SetColumn(_horizontalScroll, 0);
        Grid.SetRow(_horizontalScroll, 1);

        grid.Children.Add(_canvas);
        grid.Children.Add(_verticalScroll);
        grid.Children.Add(_horizontalScroll);
        Content = grid;

        IsTabStop = true;
        UseSystemFocusVisuals = true;

        _caretTimer.Tick += (_, _) =>
        {
            _caretVisible = !_caretVisible;
            Redraw();
        };

        AttachInput();
        AttachCompletion();
        ActualThemeChanged += (_, _) => ApplyTheme();
        Loaded += (_, _) => ApplyTheme();
    }

    /// <summary>Raised after any document mutation, including undo and programmatic edits.</summary>
    public event EventHandler? TextChanged;

    public event EventHandler? CaretMoved;

    /// <summary>Raised when a gutter mark is clicked, carrying the mark that was hit (Phase 5).</summary>
    public event EventHandler<GutterMark>? GutterMarkClicked;

    public SqlDocument Document
    {
        get => _document;
        set
        {
            if (ReferenceEquals(_document, value))
            {
                return;
            }

            _document.Changed -= OnDocumentChanged;
            _tokenizer.Detach();

            _document = value ?? new SqlDocument();
            _tokenizer = new TSqlTokenizer(_document);
            _tokenizer.Retokenized += (_, _) => InvalidateLineCache();
            _document.Changed += OnDocumentChanged;

            _caret = _anchor = 0;
            _scrollX = _scrollY = 0;
            DismissCompletion(CompletionDismissReason.DocumentReplaced);
            InvalidateLineCache();
            UpdateScrollRanges();
            Redraw();
        }
    }

    public TSqlTokenizer Tokenizer => _tokenizer;

    /// <summary>Convenience over <see cref="SqlDocument"/> for binding and for the view model.</summary>
    public string Text
    {
        get => _document.Text;
        set
        {
            if (_document.Text == (value ?? string.Empty))
            {
                return;
            }

            _document.SetText(value ?? string.Empty);
            _caret = _anchor = 0;
            DismissCompletion(CompletionDismissReason.DocumentReplaced);
            ScrollToCaret();
        }
    }

    /// <summary>Set while a capture is in flight — the plan requires editing to stop, not the caret.</summary>
    public bool IsReadOnly { get; set; }

    public int CaretOffset => _caret;

    public int SelectionStart => Math.Min(_caret, _anchor);

    public int SelectionLength => Math.Abs(_caret - _anchor);

    public bool HasSelection => _caret != _anchor;

    public string SelectedText => _document.GetText(SelectionStart, SelectionLength);

    /// <summary>Gutter marks to draw (Phase 5). Setting replaces the whole set.</summary>
    public IReadOnlyList<GutterMark> GutterMarks { get; set; } = [];

    public IReadOnlyList<InlineAnnotation> InlineAnnotations { get; set; } = [];

    /// <summary>Toggle for end-of-line annotations, which the plan calls the noisiest surface.</summary>
    public bool ShowInlineAnnotations { get; set; } = true;

    /// <summary>Compile-error squiggles (Phase 4).</summary>
    public IReadOnlyList<EditorSquiggle> Squiggles { get; set; } = [];

    public double FontSize { get; private set; } = 13;

    /// <summary>Repaints without recomputing anything. Cheap enough for the caret blink.</summary>
    public void Redraw() => _canvas.Invalidate();

    public void SetDecorations(
        IReadOnlyList<GutterMark>? marks = null,
        IReadOnlyList<InlineAnnotation>? annotations = null,
        IReadOnlyList<EditorSquiggle>? squiggles = null)
    {
        if (marks is not null) GutterMarks = marks;
        if (annotations is not null) InlineAnnotations = annotations;
        if (squiggles is not null) Squiggles = squiggles;
        Redraw();
    }

    private void CreateTextFormats()
    {
        _textFormat = new CanvasTextFormat
        {
            FontFamily = "Cascadia Mono, Consolas, Courier New",
            FontSize = (float)FontSize,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        _lineNumberFormat = new CanvasTextFormat
        {
            FontFamily = "Cascadia Mono, Consolas, Courier New",
            FontSize = (float)FontSize - 1,
            WordWrapping = CanvasWordWrapping.NoWrap,
            HorizontalAlignment = CanvasHorizontalAlignment.Right,
        };

        _annotationFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Text, Segoe UI",
            FontSize = (float)FontSize - 2,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
    }

    private void OnCreateResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        MeasureFont(sender);
        InvalidateLineCache();
        UpdateScrollRanges();
    }

    /// <summary>
    /// The font is monospaced, so one measured glyph gives the column width and the line
    /// height — which is what lets scroll extents and hit testing be arithmetic rather than
    /// a text layout per query.
    /// </summary>
    private void MeasureFont(ICanvasResourceCreator resourceCreator)
    {
        using var probe = new CanvasTextLayout(resourceCreator, "0000000000", _textFormat, 0, 0);
        _charWidth = (float)probe.LayoutBounds.Width / 10f;
        _lineHeight = (float)Math.Ceiling(probe.LayoutBounds.Height);
        if (_charWidth <= 0) _charWidth = 8f;
        if (_lineHeight <= 0) _lineHeight = 17f;
    }

    private void ApplyTheme()
    {
        _theme = SqlSyntaxTheme.For(ActualTheme == ElementTheme.Dark);
        InvalidateLineCache();
        Redraw();
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (e.IsWholeDocument)
        {
            InvalidateLineCache();
        }
        else
        {
            // A single-line edit only invalidates that line; anything that changed the line
            // count shifts every cached layout below it, so those go too.
            if (e.EndLineAfter != e.EndLineBefore)
            {
                InvalidateLineCache();
            }
            else
            {
                for (var line = e.StartLine; line <= e.EndLineAfter; line++)
                {
                    DisposeLayout(line);
                }
            }
        }

        UpdateScrollRanges();
        TextChanged?.Invoke(this, EventArgs.Empty);
        NotifyTextChangedToEditContext(e);

        if (FrameworkElementAutomationPeer.FromElement(this) is SqlEditorAutomationPeer peer)
        {
            peer.RaiseTextChanged();
        }

        Redraw();
    }

    private void InvalidateLineCache()
    {
        foreach (var layout in _lineLayouts.Values)
        {
            layout.Dispose();
        }

        _lineLayouts.Clear();
        Redraw();
    }

    private void DisposeLayout(int line)
    {
        if (_lineLayouts.Remove(line, out var layout))
        {
            layout.Dispose();
        }
    }

    private CanvasTextLayout GetLineLayout(ICanvasResourceCreator resourceCreator, int line)
    {
        if (_lineLayouts.TryGetValue(line, out var cached))
        {
            return cached;
        }

        var text = _document.GetLineText(line);
        var layout = new CanvasTextLayout(resourceCreator, text, _textFormat, 0, 0);

        foreach (var span in _tokenizer.SpansForLine(line))
        {
            var start = Math.Clamp(span.Start, 0, text.Length);
            var length = Math.Clamp(span.Length, 0, text.Length - start);
            if (length > 0)
            {
                layout.SetColor(start, length, _theme.For(span.Class));
            }
        }

        // Bound the cache: only visible lines are ever asked for, but a long session of
        // scrolling would otherwise accumulate every line the user passed.
        if (_lineLayouts.Count > 400)
        {
            InvalidateLineCache();
        }

        _lineLayouts[line] = layout;
        return layout;
    }

    // ---- Geometry ----------------------------------------------------------

    private float TextOriginX => _gutterWidth + TextLeftPadding;

    private double ViewportWidth => Math.Max(0, _canvas.ActualWidth - TextOriginX);

    private double ViewportHeight => Math.Max(0, _canvas.ActualHeight);

    private int FirstVisibleLine => Math.Max(0, (int)(_scrollY / _lineHeight) - OverdrawLines);

    private int LastVisibleLine => Math.Min(
        _document.LineCount - 1,
        (int)((_scrollY + ViewportHeight) / _lineHeight) + OverdrawLines);

    /// <summary>Top of a line in canvas coordinates.</summary>
    private float LineTop(int line) => (float)((line * _lineHeight) - _scrollY);

    private float ColumnX(int column) => (float)(TextOriginX + (column * _charWidth) - _scrollX);

    /// <summary>Document offset under a canvas point, clamped into the document.</summary>
    private int OffsetAt(Point point)
    {
        var line = Math.Clamp(
            (int)Math.Floor((point.Y + _scrollY) / _lineHeight),
            0,
            _document.LineCount - 1);

        var column = (int)Math.Round((point.X - TextOriginX + _scrollX) / _charWidth);
        return _document.OffsetOf(line, Math.Max(0, column));
    }

    private void UpdateScrollRanges()
    {
        var contentHeight = _document.LineCount * _lineHeight;
        var maxScrollY = Math.Max(0, contentHeight - ViewportHeight);

        var longestLine = 0;
        for (var i = 0; i < _document.LineCount; i++)
        {
            longestLine = Math.Max(longestLine, _document.GetLineLength(i));
        }

        var contentWidth = (longestLine + 4) * _charWidth;
        var maxScrollX = Math.Max(0, contentWidth - ViewportWidth);

        _verticalScroll.Minimum = 0;
        _verticalScroll.Maximum = maxScrollY;
        _verticalScroll.ViewportSize = ViewportHeight;
        _verticalScroll.SmallChange = _lineHeight;
        _verticalScroll.LargeChange = Math.Max(_lineHeight, ViewportHeight - _lineHeight);
        _verticalScroll.Visibility = maxScrollY > 0.5 ? Visibility.Visible : Visibility.Collapsed;

        _horizontalScroll.Minimum = 0;
        _horizontalScroll.Maximum = maxScrollX;
        _horizontalScroll.ViewportSize = ViewportWidth;
        _horizontalScroll.SmallChange = _charWidth * 4;
        _horizontalScroll.LargeChange = Math.Max(_charWidth, ViewportWidth - _charWidth);
        _horizontalScroll.Visibility = maxScrollX > 0.5 ? Visibility.Visible : Visibility.Collapsed;

        // The gutter grows with the line count so a 4-digit plan does not clip its numbers.
        var digits = Math.Max(2, _document.LineCount.ToString().Length);
        _gutterWidth = GutterMarkWidth + (digits * _charWidth) + (LineNumberPadding * 2);

        SetScroll(Math.Min(_scrollX, maxScrollX), Math.Min(_scrollY, maxScrollY));
    }

    private void SetScroll(double x, double y)
    {
        _scrollX = Math.Clamp(x, 0, Math.Max(0, _horizontalScroll.Maximum));
        _scrollY = Math.Clamp(y, 0, Math.Max(0, _verticalScroll.Maximum));
        _verticalScroll.Value = _scrollY;
        _horizontalScroll.Value = _scrollX;
        NotifyLayoutChangedToEditContext();
        Redraw();
    }

    /// <summary>Brings the caret into view with a line of margin, the way every editor does.</summary>
    public void ScrollToCaret()
    {
        var (line, column) = _document.PositionOf(_caret);
        var targetY = _scrollY;
        var top = line * _lineHeight;

        if (top < _scrollY)
        {
            targetY = top;
        }
        else if (top + _lineHeight > _scrollY + ViewportHeight)
        {
            targetY = top + _lineHeight - ViewportHeight;
        }

        var targetX = _scrollX;
        var caretX = column * _charWidth;
        if (caretX < _scrollX)
        {
            targetX = Math.Max(0, caretX - (_charWidth * 4));
        }
        else if (caretX + _charWidth > _scrollX + ViewportWidth)
        {
            targetX = caretX + _charWidth - ViewportWidth + (_charWidth * 4);
        }

        SetScroll(targetX, targetY);
    }

    public void ScrollToLine(int line)
    {
        line = Math.Clamp(line, 0, Math.Max(0, _document.LineCount - 1));
        SetScroll(_scrollX, Math.Max(0, (line * _lineHeight) - (ViewportHeight / 3)));
    }

    /// <summary>Caret rectangle in this control's coordinates — the anchor for the completion popup.</summary>
    public Rect CaretRect()
    {
        var (line, column) = _document.PositionOf(_caret);
        return new Rect(ColumnX(column), LineTop(line), 1, _lineHeight);
    }

    // ---- Drawing -----------------------------------------------------------

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var width = (float)sender.ActualWidth;
        var height = (float)sender.ActualHeight;

        ds.FillRectangle(0, 0, width, height, _theme.Background);

        if (_document.LineCount == 0)
        {
            return;
        }

        var first = FirstVisibleLine;
        var last = LastVisibleLine;
        var caretLine = _document.LineOf(_caret);
        var selectionStart = SelectionStart;
        var selectionEnd = selectionStart + SelectionLength;

        // Current-line wash, drawn under everything else and only when nothing is selected —
        // two highlights fighting over the same line reads as a rendering bug.
        if (!HasSelection && caretLine >= first && caretLine <= last)
        {
            ds.FillRectangle(TextOriginX, LineTop(caretLine), width, _lineHeight, _theme.CurrentLineFill);
        }

        for (var line = first; line <= last; line++)
        {
            var y = LineTop(line);
            var lineStart = _document.GetLineStart(line);
            var lineEnd = _document.GetLineEnd(line);

            // Selection, clipped to this line. A selection that runs past the end of a line
            // draws one extra column so the newline itself looks selected.
            if (SelectionLength > 0 && selectionEnd > lineStart && selectionStart <= lineEnd)
            {
                var from = Math.Max(selectionStart, lineStart) - lineStart;
                var to = Math.Min(selectionEnd, lineEnd) - lineStart;
                var trailingNewline = selectionEnd > lineEnd ? 1 : 0;
                var x0 = ColumnX(from);
                var x1 = ColumnX(to + trailingNewline);
                ds.FillRectangle(x0, y, Math.Max(2f, x1 - x0), _lineHeight, _theme.SelectionFill);
            }

            var layout = GetLineLayout(sender, line);
            ds.DrawTextLayout(layout, ColumnX(0), y, _theme.Foreground);

            DrawSquigglesForLine(ds, line, lineStart, lineEnd, y);
            DrawInlineAnnotationForLine(ds, line, lineEnd - lineStart, y);
        }

        DrawGutter(ds, first, last, caretLine, height);
        DrawCaret(ds, caretLine);
    }

    private void DrawGutter(CanvasDrawingSession ds, int first, int last, int caretLine, float height)
    {
        ds.FillRectangle(0, 0, _gutterWidth, height, _theme.GutterBackground);
        ds.DrawLine(_gutterWidth, 0, _gutterWidth, height, _theme.Divider);

        var numberWidth = _gutterWidth - GutterMarkWidth - LineNumberPadding;

        for (var line = first; line <= last; line++)
        {
            var y = LineTop(line);
            var color = line == caretLine ? _theme.LineNumberActive : _theme.LineNumber;

            using var layout = new CanvasTextLayout(
                ds,
                (line + 1).ToString(System.Globalization.CultureInfo.CurrentCulture),
                _lineNumberFormat,
                numberWidth,
                _lineHeight);
            ds.DrawTextLayout(layout, GutterMarkWidth, y, color);
        }

        // Marks sit in their own strip at the far left, so they never collide with numbers.
        foreach (var mark in GutterMarks)
        {
            if (mark.Line < first || mark.Line > last)
            {
                continue;
            }

            var y = LineTop(mark.Line);
            var color = MarkColor(mark.Kind);
            var cy = y + (_lineHeight / 2);

            if (mark.Kind == GutterMarkKind.Added)
            {
                ds.FillRectangle(3f, y + 2f, 4f, _lineHeight - 4f, color);
            }
            else if (mark.Kind == GutterMarkKind.Error)
            {
                ds.FillCircle(6f, cy, 4f, color);
            }
            else
            {
                // A triangle: up for improved, down for regressed. Direction has to survive
                // being read in greyscale, so shape carries the meaning as well as colour.
                var up = mark.Kind == GutterMarkKind.Improved;
                var tip = up ? cy - 4f : cy + 4f;
                var baseY = up ? cy + 3f : cy - 3f;
                using var path = new Microsoft.Graphics.Canvas.Geometry.CanvasPathBuilder(ds);
                path.BeginFigure(6f, tip);
                path.AddLine(1.5f, baseY);
                path.AddLine(10.5f, baseY);
                path.EndFigure(Microsoft.Graphics.Canvas.Geometry.CanvasFigureLoop.Closed);
                using var geometry = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreatePath(path);
                ds.FillGeometry(geometry, color);
            }
        }
    }

    private Color MarkColor(GutterMarkKind kind) => kind switch
    {
        GutterMarkKind.Improved => _theme.Improved,
        GutterMarkKind.Regressed => _theme.Regressed,
        GutterMarkKind.Error => _theme.Error,
        _ => _theme.Added,
    };

    private void DrawSquigglesForLine(CanvasDrawingSession ds, int line, int lineStart, int lineEnd, float y)
    {
        foreach (var squiggle in Squiggles)
        {
            var start = squiggle.Start;
            var end = squiggle.Start + Math.Max(1, squiggle.Length);
            if (end <= lineStart || start > lineEnd)
            {
                continue;
            }

            var from = Math.Max(start, lineStart) - lineStart;
            var to = Math.Max(from + 1, Math.Min(end, lineEnd) - lineStart);
            var x0 = ColumnX(from);
            var x1 = ColumnX(to);
            var baseline = y + _lineHeight - 2f;

            // Hand-drawn zig-zag: Win2D has no squiggle stroke style, and a flat underline
            // is too easily mistaken for a hyperlink.
            var up = true;
            for (var x = x0; x < x1; x += 3f)
            {
                ds.DrawLine(x, up ? baseline : baseline - 2.5f, Math.Min(x + 3f, x1), up ? baseline - 2.5f : baseline, _theme.Error, 1.2f);
                up = !up;
            }
        }
    }

    private void DrawInlineAnnotationForLine(CanvasDrawingSession ds, int line, int lineLength, float y)
    {
        if (!ShowInlineAnnotations)
        {
            return;
        }

        foreach (var annotation in InlineAnnotations)
        {
            if (annotation.Line != line || string.IsNullOrWhiteSpace(annotation.Text))
            {
                continue;
            }

            var x = ColumnX(lineLength + 3);
            ds.DrawText(annotation.Text, x, y, MarkColor(annotation.Kind), _annotationFormat);
        }
    }

    private void DrawCaret(CanvasDrawingSession ds, int caretLine)
    {
        if (!_hasFocus || !_caretVisible || IsReadOnly)
        {
            return;
        }

        var rect = CaretRect();
        if (rect.X < _gutterWidth)
        {
            return;
        }

        ds.FillRectangle((float)rect.X, (float)rect.Y, 1.6f, _lineHeight, _theme.Caret);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SqlEditorAutomationPeer(this);
}
