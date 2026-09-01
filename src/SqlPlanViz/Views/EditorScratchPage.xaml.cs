using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SqlPlanViz.Editing;
using SqlPlanViz.Editing.Completion;

namespace SqlPlanViz.Views;

/// <summary>
/// Standalone host for <see cref="SqlPlanViz.Controls.SqlEditorControl"/> (Phase 1 deliverable).
/// Nothing here is wired to a plan, a connection, or the diagnostics layer on purpose — the
/// point is to judge the editor in isolation.
/// </summary>
public sealed partial class EditorScratchPage : Page
{
    private const string Sample = """
        -- Phase 1 scratch sample: highlighting, undo, selection, IME.
        DECLARE @CustomerId INT = 42;

        SELECT TOP (100)
               o.OrderId,
               o.OrderDate,
               SUM(l.Quantity * l.UnitPrice) AS LineTotal
        FROM   dbo.Orders AS o
               INNER JOIN dbo.OrderLines AS l
                   ON l.OrderId = o.OrderId
        WHERE  o.CustomerId = @CustomerId
               AND o.OrderDate >= DATEADD(MONTH, -6, GETDATE())
        GROUP BY o.OrderId, o.OrderDate
        HAVING SUM(l.Quantity * l.UnitPrice) > 1000.00
        ORDER BY LineTotal DESC;

        /* A block comment,
           spanning lines, to exercise the incremental tokenizer. */
        """;

    public EditorScratchPage()
    {
        InitializeComponent();

        // Phase 2's deliverable is completion with no server: keywords always, plus whatever
        // objects a loaded plan named. Nothing here connects to anything.
        var engine = new CompletionEngine();
        engine.Register(new KeywordProvider());
        engine.Register(new PlanObjectProvider());
        Editor.CompletionEngine = engine;

        Editor.Text = Sample;
        Editor.CaretMoved += (_, _) => UpdateStatus();
        Editor.TextChanged += (_, _) => UpdateStatus();

        // Decorations belong to Phases 4 and 5; a couple of fixed ones here prove the gutter
        // and annotation columns draw before anything computes real values for them.
        Editor.SetDecorations(
            marks:
            [
                new GutterMark { Line = 5, Kind = GutterMarkKind.Regressed, Tooltip = "sample" },
                new GutterMark { Line = 8, Kind = GutterMarkKind.Improved, Tooltip = "sample" },
            ],
            annotations: [new InlineAnnotation { Line = 5, Text = "sample annotation", Kind = GutterMarkKind.Regressed }]);

        Loaded += (_, _) => Editor.Focus(FocusState.Programmatic);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var (line, column) = Editor.Document.PositionOf(Editor.CaretOffset);
        var selection = Editor.SelectionLength > 0 ? $"  ·  {Editor.SelectionLength} selected" : string.Empty;
        StatusText.Text = $"Ln {line + 1}, Col {column + 1}{selection}  ·  {Editor.Document.LineCount} lines"
                          + $"  ·  {(Editor.Document.CanUndo ? "undo available" : "nothing to undo")}";
    }

    private void OnLoadSample(object sender, RoutedEventArgs e) => Editor.Text = Sample;

    private void OnToggleAnnotations(object sender, RoutedEventArgs e)
    {
        Editor.ShowInlineAnnotations = AnnotationToggle.IsChecked == true;
        Editor.Redraw();
    }

    private void OnToggleReadOnly(object sender, RoutedEventArgs e)
    {
        Editor.IsReadOnly = ReadOnlyToggle.IsChecked == true;
        Editor.Redraw();
    }
}
