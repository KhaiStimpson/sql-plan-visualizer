using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SqlPlanViz.Common;
using SqlPlanViz.Controls;
using SqlPlanViz.Diagnostics;
using SqlPlanViz.Model;
using Windows.UI;

namespace SqlPlanViz.Views;

/// <summary>
/// A peer view to <see cref="PlanCanvas"/> (hot-path-plan.md Phase 2): the same plan sorted by
/// time instead of shape. Frames lay out as plain XAML rectangles on a <see cref="Canvas"/> —
/// unlike the tree view, a flame graph over a realistic plan (dozens to a few hundred nodes)
/// doesn't need Win2D's viewport virtualization, so this stays much simpler than
/// <see cref="PlanCanvas"/>.
/// </summary>
public sealed partial class FlameView : UserControl
{
    private const double RowHeight = 26;
    private const double RowGap = 3;

    private readonly Dictionary<int, Border> _frameElements = new();
    private PlanPalette _palette = PlanPalette.For(false);
    private TimeAttributionBasis _basis = TimeAttributionBasis.Elapsed;

    public FlameView()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => Rebuild();
    }

    public static readonly DependencyProperty StatementProperty = DependencyProperty.Register(
        nameof(Statement),
        typeof(PlanStatement),
        typeof(FlameView),
        new PropertyMetadata(null, (d, _) => ((FlameView)d).Rebuild()));

    public PlanStatement? Statement
    {
        get => (PlanStatement?)GetValue(StatementProperty);
        set => SetValue(StatementProperty, value);
    }

    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(
        nameof(SelectedNode),
        typeof(PlanNode),
        typeof(FlameView),
        new PropertyMetadata(null, (d, e) => ((FlameView)d).OnSelectedNodeChanged(e.OldValue as PlanNode, e.NewValue as PlanNode)));

    public PlanNode? SelectedNode
    {
        get => (PlanNode?)GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    private void OnBasisChanged(object sender, RoutedEventArgs e)
    {
        _basis = sender switch
        {
            RadioButton { Content: "CPU time" } => TimeAttributionBasis.Cpu,
            RadioButton { Content: "Rows read" } => TimeAttributionBasis.RowsRead,
            _ => TimeAttributionBasis.Elapsed,
        };

        // Checked fires while the XAML is still being built; the first real build follows.
        if (FlameCanvas is not null)
        {
            Rebuild();
        }
    }

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e) => Rebuild();

    private void OnSelectedNodeChanged(PlanNode? oldNode, PlanNode? newNode) => Rebuild();

    /// <summary>The operator with the most self time: its frame gets an accent outline.</summary>
    private int? _hotNodeId;

    private void Rebuild()
    {
        _palette = PlanPalette.For(ActualTheme == ElementTheme.Dark);
        FlameCanvas.Children.Clear();
        _frameElements.Clear();

        var statement = Statement;
        var available = statement?.HasRuntimeStats == true;
        DisabledPanel.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        FlameScroller.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        BasisSelector.IsHitTestVisible = available;
        BasisSelector.Opacity = available ? 1 : 0.45;

        if (!available || statement is null)
        {
            ApproximateWarning.Visibility = Visibility.Collapsed;
            NegativeSelfWarning.Visibility = Visibility.Collapsed;
            return;
        }

        var result = TimeAttribution.Build(statement, _basis);
        ApproximateWarning.Visibility = result.Frames.Any(f => f.IsApproximate) ? Visibility.Visible : Visibility.Collapsed;
        NegativeSelfWarning.Visibility = result.ClampedNegativeSelfCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (result.ClampedNegativeSelfCount > 0)
        {
            NegativeSelfWarningBody.Text =
                $"{result.ClampedNegativeSelfCount} operator(s) have children whose combined width " +
                "exceeds their own — this basis doesn't nest cleanly here, so a child bar may extend " +
                "past its parent's. Treat the numbers as a rough guide, not an exact partition.";
        }

        if (result.Frames.Count == 0)
        {
            return;
        }

        var root = result.Frames[0];
        // A basis that doesn't nest cleanly (ClampedNegativeSelfCount > 0) can produce a child
        // frame that extends past the root's own width — scale to the true max extent so
        // nothing renders off-canvas.
        var totalWidth = Math.Max(1, Math.Max(root.Width, result.Frames.Max(f => f.Offset + f.Width)));
        var canvasWidthPx = Math.Max(200, FlameScroller.ActualWidth - 32);
        var pxPerUnit = canvasWidthPx / totalWidth;

        var nodesById = statement.AllNodes.ToDictionary(n => n.NodeId);
        var maxDepth = result.Frames.Max(f => f.Depth);
        _hotNodeId = statement.AllNodes.MaxBy(n => n.SelfTimeMs ?? 0)?.NodeId;

        foreach (var frame in result.Frames)
        {
            if (!nodesById.TryGetValue(frame.NodeId, out var node))
            {
                continue;
            }

            var widthPx = Math.Max(2, frame.Width * pxPerUnit);
            var xPx = frame.Offset * pxPerUnit;
            var yPx = frame.Depth * (RowHeight + RowGap);

            // The sequential tm-1 … tm-9 ramp: the widest frames take the strongest step, and
            // tm-fg reads on every step. Selected is the accent with a sel-edge; the hottest
            // frame (largest self share) gets an accent outline.
            var fraction = totalWidth <= 0 ? 0 : frame.Width / totalWidth;
            var isSelected = ReferenceEquals(SelectedNode, node);
            var isHot = frame.NodeId == _hotNodeId;
            var fill = isSelected ? _palette.Selection : _palette.RampStep(fraction);
            var outline = isSelected ? _palette.SelEdge : isHot ? _palette.Selection : _palette.Surface;

            var border = new Border
            {
                Width = widthPx,
                Height = RowHeight,
                Background = new SolidColorBrush(fill),
                BorderBrush = new SolidColorBrush(outline),
                BorderThickness = new Thickness(isSelected || isHot ? 2 : 1),
                CornerRadius = new CornerRadius(3),
                Opacity = frame.IsApproximate && !isSelected ? 0.7 : 1,
                Tag = node,
            };

            var label = BuildLabel(node, _basis);
            var text = new TextBlock
            {
                Text = label,
                Margin = new Thickness(5, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(isSelected ? _palette.Tokens.AccentInk : _palette.Tokens.TmFg),
                FontWeight = isSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                FontSize = 11.5,
            };
            border.Child = text;

            ToolTipService.SetToolTip(border, label + (frame.IsApproximate ? " (approximate under parallelism)" : string.Empty));

            border.PointerPressed += (_, args) =>
            {
                SelectedNode = node;
                args.Handled = true;
            };

            Canvas.SetLeft(border, xPx);
            Canvas.SetTop(border, yPx);
            FlameCanvas.Children.Add(border);
            _frameElements[frame.NodeId] = border;
        }

        FlameCanvas.Width = canvasWidthPx;
        FlameCanvas.Height = ((maxDepth + 1) * (RowHeight + RowGap)) + 8;
    }

    private static string BuildLabel(PlanNode node, TimeAttributionBasis basis)
    {
        var parts = new List<string> { node.PhysicalOp };
        if (!string.IsNullOrEmpty(node.ObjectName))
        {
            parts.Add(NodeLabeller.TruncateObjectName(node.ObjectName));
        }

        parts.Add(basis switch
        {
            TimeAttributionBasis.Elapsed => node.ActualElapsedMs is double e ? Format.Milliseconds(e) : "—",
            TimeAttributionBasis.Cpu => node.ActualCpuMs is double c ? Format.Milliseconds(c) : "—",
            TimeAttributionBasis.RowsRead => node.ActualRows is double r ? $"{Format.Rows(r)} rows" : "—",
            _ => "—",
        });

        if (node.ActualExecutions is int ex && ex > 1)
        {
            parts.Add($"×{ex:N0}");
        }

        return string.Join(" · ", parts);
    }
}
