using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SqlPlanViz.Common;
using SqlPlanViz.Diagnostics;
using SqlPlanViz.Layout;
using SqlPlanViz.Model;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace SqlPlanViz.Controls;

/// <summary>
/// The plan tree (TDD §8), drawn immediate-mode on Win2D/Direct2D.
///
/// The TDD specified WPF's <c>DrawingVisual</c>; WinUI 3 has no retained drawing-visual
/// layer, and Win2D's <see cref="CanvasControl"/> is the direct equivalent — a GPU-composited
/// surface with a per-frame <see cref="CanvasDrawingSession"/>. The performance strategy of
/// §8 carries over intact and is implemented here:
///
///   1. No per-node XAML element — every operator is drawn, not templated.
///   2. Viewport virtualization: only nodes/edges intersecting the visible rect are drawn.
///   3. The pan/zoom transform is applied to the drawing session, so nothing re-layouts.
///   4. Immutable plan data means geometry is built once per layout and reused per frame.
///   5. Tree layout runs once per plan (and again only on collapse/expand).
///   6. Collapse/expand keeps huge plans from ever being realized.
///   7. Level-of-detail: below ~0.45 zoom, text drawing is skipped entirely.
/// </summary>
public sealed partial class PlanCanvas : UserControl
{
    private const float AccentWidth = 6f;
    private const float NodeCorner = 8f;
    private const float TextLeftPad = AccentWidth + 12f;
    private const float TextRightPad = 12f;
    private const float LodTextThreshold = 0.45f;

    /// <summary>Below this zoom, nodes show the operator name only (hot-path-plan.md Phase 1) — above §8.7's all-text-off threshold but too dense for the full card.</summary>
    private const float LodDetailThreshold = 0.75f;
    private const float MinScale = 0.05f;
    private const float MaxScale = 3.0f;

    private static readonly CanvasStrokeStyle DashedStroke = new() { DashStyle = CanvasDashStyle.Dash };

    private readonly CanvasControl _canvas = new();
    private readonly PlanLayoutEngine _engine = new();
    private readonly HashSet<int> _collapsed = [];
    private HashSet<int> _hotPathNodeIds = [];
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly ToolTip _operatorToolTip = new()
    {
        MaxWidth = 380,
        Placement = Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Bottom,
    };

    private PlanLayout? _layout;
    private PlanNode? _focusRoot;
    private PlanPalette _palette = PlanPalette.For(isDark: false);

    private float _scale = 1f;
    private Vector2 _offset = Vector2.Zero;

    private bool _pointerDown;
    private Point _dragStart;
    private Vector2 _dragOffsetStart;
    private double _dragDistance;
    private PlanNode? _hoveredNode;
    private IReadOnlyList<EdgeLayout> _playbackEdges = [];
    private IReadOnlyList<double> _playbackDurationsMs = [];
    private int _playbackIndex;
    private double _playbackProgress;
    private DateTime _playbackStepStartedUtc;
    private Rect _minimapBounds;
    private Vector2 _minimapPlanOrigin;
    private float _minimapScale;
    private int _findingCursor = -1;
    private PlanDiffResult? _diff;
    private Dictionary<PlanNode, PlanDiffKind> _diffKinds = [];
    private PlanLayout? _diffBeforeLayout;

    // Cached Win2D resources. Node geometry is shared by every node because all nodes are
    // the same size; edges are cached per layout since they only move when layout re-runs.
    private CanvasGeometry? _nodeGeometry;
    private CanvasGeometry? _accentGeometry;
    private CanvasGeometry? _warningGeometry;
    private Dictionary<EdgeLayout, CanvasGeometry>? _edgeGeometries;

    private CanvasTextFormat _titleFormat = null!;
    private CanvasTextFormat _subtitleFormat = null!;
    private CanvasTextFormat _metaFormat = null!;
    private CanvasTextFormat _badgeFormat = null!;
    private CanvasTextFormat _expanderFormat = null!;

    private double _metricMax;

    /// <summary>
    /// A plan can be set before the canvas has been measured, and fitting to a zero-sized
    /// viewport does nothing — so the fit is deferred until there's a real size to fit to.
    /// </summary>
    private bool _pendingFit;

    /// <summary>
    /// Set once the user pans or zooms. Until then the view is still "auto", so a resize
    /// (the detail pane appearing, the window being dragged wider) re-fits rather than
    /// leaving the plan stranded off-centre.
    /// </summary>
    private bool _userAdjusted;

    public PlanCanvas()
    {
        CreateTextFormats();

        // Transparent so the window's Mica backdrop shows through behind the plan.
        _canvas.ClearColor = Microsoft.UI.Colors.Transparent;
        _canvas.CreateResources += OnCreateResources;
        _canvas.Draw += OnDraw;

        Content = _canvas;
        IsTabStop = true;
        UseSystemFocusVisuals = true;

        _operatorToolTip.IsEnabled = false;
        ToolTipService.SetToolTip(_canvas, _operatorToolTip);

        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerExited += OnPointerExited;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerCaptureLost += OnPointerCaptureLost;
        _canvas.PointerWheelChanged += OnPointerWheelChanged;
        _canvas.DoubleTapped += OnDoubleTapped;
        _canvas.SizeChanged += (_, _) => TryPendingFit();
        _playbackTimer.Tick += OnPlaybackTick;

        KeyDown += OnKeyDown;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => RefreshPalette();
    }

    /// <summary>Raised when the user asks to collapse/expand, so the shell can update counts.</summary>
    public event EventHandler? LayoutChanged;

    public event EventHandler? PlaybackChanged;

    public event EventHandler? FocusChanged;

    public event EventHandler? SearchRequested;

    #region Dependency properties

    public static readonly DependencyProperty StatementProperty = DependencyProperty.Register(
        nameof(Statement),
        typeof(PlanStatement),
        typeof(PlanCanvas),
        new PropertyMetadata(null, (d, _) => ((PlanCanvas)d).OnStatementChanged()));

    public PlanStatement? Statement
    {
        get => (PlanStatement?)GetValue(StatementProperty);
        set => SetValue(StatementProperty, value);
    }

    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(
        nameof(SelectedNode),
        typeof(PlanNode),
        typeof(PlanCanvas),
        new PropertyMetadata(null, (d, _) => ((PlanCanvas)d).Redraw()));

    public PlanNode? SelectedNode
    {
        get => (PlanNode?)GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    public static readonly DependencyProperty FilterTextProperty = DependencyProperty.Register(
        nameof(FilterText),
        typeof(string),
        typeof(PlanCanvas),
        new PropertyMetadata(string.Empty, (d, _) => ((PlanCanvas)d).Redraw()));

    public string FilterText
    {
        get => (string)GetValue(FilterTextProperty);
        set => SetValue(FilterTextProperty, value);
    }

    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(
        nameof(Metric),
        typeof(SizeMetric),
        typeof(PlanCanvas),
        new PropertyMetadata(SizeMetric.SubtreeCost, (d, _) => ((PlanCanvas)d).OnMetricChanged()));

    public SizeMetric Metric
    {
        get => (SizeMetric)GetValue(MetricProperty);
        set => SetValue(MetricProperty, value);
    }

    public static readonly DependencyProperty ColorModeProperty = DependencyProperty.Register(
        nameof(ColorMode),
        typeof(ColorMode),
        typeof(PlanCanvas),
        new PropertyMetadata(ColorMode.Metric, (d, _) => ((PlanCanvas)d).Redraw()));

    /// <summary>Metric heat map, or the finding-driven blame overlay (tuning-roadmap.md Phase 4.5).</summary>
    public ColorMode ColorMode
    {
        get => (ColorMode)GetValue(ColorModeProperty);
        set => SetValue(ColorModeProperty, value);
    }

    public static readonly DependencyProperty LabelDetailProperty = DependencyProperty.Register(
        nameof(LabelDetail),
        typeof(LabelDetail),
        typeof(PlanCanvas),
        new PropertyMetadata(LabelDetail.Full, (d, _) => ((PlanCanvas)d).Redraw()));

    /// <summary>User-selected text density, capped further by zoom (hot-path-plan.md Phase 1).</summary>
    public LabelDetail LabelDetail
    {
        get => (LabelDetail)GetValue(LabelDetailProperty);
        set => SetValue(LabelDetailProperty, value);
    }

    #endregion

    public float ZoomPercent => _scale * 100f;

    public bool IsPlaybackRunning => _playbackTimer.IsEnabled;

    public bool IsFocused => _focusRoot is not null && !ReferenceEquals(_focusRoot, Statement?.Root);

    public string FocusBreadcrumb => IsFocused
        ? $"Whole plan  /  {_focusRoot!.PhysicalOp} (node {_focusRoot.NodeId})"
        : "Whole plan";

    #region Public commands

    public void FitToView()
    {
        if (_layout is null || _canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0)
        {
            // Nothing to fit to yet — retry when the canvas gets a size.
            _pendingFit = _layout is not null;
            return;
        }

        var sx = _canvas.ActualWidth / _layout.Width;
        var sy = _canvas.ActualHeight / _layout.Height;
        _scale = Math.Clamp((float)Math.Min(sx, sy), MinScale, 1f);
        _pendingFit = false;
        _userAdjusted = false;
        CenterContent();
        Redraw();
    }

    private void TryPendingFit()
    {
        if (_pendingFit || !_userAdjusted)
        {
            FitToView();
        }
        else
        {
            Redraw();
        }
    }

    public void ZoomBy(float factor) => ZoomAbout(
        factor,
        new Point(_canvas.ActualWidth / 2, _canvas.ActualHeight / 2));

    public void ResetZoom()
    {
        _scale = 1f;
        CenterContent();
        Redraw();
    }

    public void CollapseAll()
    {
        if (Statement is null)
        {
            return;
        }

        _collapsed.Clear();

        // Collapse the deepest branching points rather than the root — collapsing the root
        // just hides the entire plan, which is never what "collapse all" is meant to do.
        foreach (var node in Statement.AllNodes)
        {
            if (node.Children.Count > 0 && node != Statement.Root)
            {
                _collapsed.Add(node.NodeId);
            }
        }

        RebuildLayout(preserveView: false);
    }

    public void ExpandAll()
    {
        _collapsed.Clear();
        RebuildLayout(preserveView: false);
    }

    public void CollapseCheapSubtrees(double threshold = 0.02)
    {
        _collapsed.Clear();
        AddCheapSubtrees(threshold);
        RebuildLayout(preserveView: false);
    }

    public void FocusHotPath()
    {
        _collapsed.Clear();
        ComputeHotPath();
        CollapseOutsideHotPath();
        RebuildLayout(preserveView: false);
    }

    public void FocusOnSubtree(PlanNode node)
    {
        _focusRoot = node;
        _collapsed.Remove(node.NodeId);
        RebuildLayout(preserveView: false);
        FitToView();
        FocusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ExitFocus()
    {
        if (!IsFocused)
        {
            return;
        }

        _focusRoot = null;
        RebuildLayout(preserveView: false);
        FitToView();
        FocusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetDiff(PlanDiffResult? diff)
    {
        _diff = diff;
        _diffKinds = diff?.Nodes
            .Where(delta => delta.After is not null)
            .ToDictionary(delta => delta.After!, delta => delta.Kind)
            ?? [];
        _diffBeforeLayout = diff is null ? null : _engine.Layout(diff.Before.Root);
        Redraw();
    }

    private void AddCheapSubtrees(double threshold = 0.02)
    {
        if (Statement is null)
        {
            return;
        }

        var total = Math.Max(Statement.Summary.TotalSubtreeCost, Statement.Root.EstimatedSubtreeCost);
        if (total <= 0)
        {
            return;
        }

        foreach (var node in Statement.AllNodes)
        {
            if (!ReferenceEquals(node, Statement.Root)
                && node.Children.Count > 0
                && node.EstimatedSubtreeCost / total < threshold)
            {
                _collapsed.Add(node.NodeId);
            }
        }
    }

    private void ComputeHotPath()
    {
        _hotPathNodeIds = [];
        var node = Statement?.Root;
        while (node is not null)
        {
            _hotPathNodeIds.Add(node.NodeId);
            node = node.Children.MaxBy(child => child.EstimatedSubtreeCost);
        }
    }

    private void CollapseOutsideHotPath()
    {
        if (Statement is null)
        {
            return;
        }

        foreach (var node in Statement.AllNodes)
        {
            if (!_hotPathNodeIds.Contains(node.NodeId) && node.Children.Count > 0)
            {
                _collapsed.Add(node.NodeId);
            }
        }
    }

    public void ToggleCollapse(PlanNode node)
    {
        if (node.Children.Count == 0)
        {
            return;
        }

        if (!_collapsed.Remove(node.NodeId))
        {
            _collapsed.Add(node.NodeId);
        }

        RebuildLayout(preserveView: true);
    }

    public bool IsCollapsed(PlanNode node) => _collapsed.Contains(node.NodeId);

    public void TogglePlayback()
    {
        if (IsPlaybackRunning)
        {
            StopPlayback();
        }
        else
        {
            StartPlayback();
        }
    }

    public static readonly DependencyProperty ExplanationVerbosityProperty = DependencyProperty.Register(
        nameof(ExplanationVerbosity),
        typeof(ExplanationVerbosity),
        typeof(PlanCanvas),
        new PropertyMetadata(ExplanationVerbosity.Expansive, (d, _) => ((PlanCanvas)d).ClearOperatorHover()));

    public ExplanationVerbosity ExplanationVerbosity
    {
        get => (ExplanationVerbosity)GetValue(ExplanationVerbosityProperty);
        set => SetValue(ExplanationVerbosityProperty, value);
    }

    public void StartPlayback()
    {
        StopPlayback();
        if (_layout is null || _layout.Edges.Count == 0)
        {
            return;
        }

        _playbackEdges = ExecutionOrderEdges(_layout);
        var weights = _playbackEdges
            .Select(e => Math.Max(1, e.Child.Node.SelfTimeMs
                                      ?? e.Child.Node.ActualElapsedMs
                                      ?? e.Child.Node.EstimatedOperatorCost * 1000))
            .ToList();
        var totalWeight = weights.Sum();
        _playbackDurationsMs = weights
            .Select(weight => Math.Clamp(8000 * weight / totalWeight, 180, 1600))
            .ToList();
        _playbackIndex = 0;
        _playbackProgress = 0;
        _playbackStepStartedUtc = DateTime.UtcNow;
        _playbackTimer.Start();
        PlaybackChanged?.Invoke(this, EventArgs.Empty);
        Redraw();
    }

    public void StopPlayback()
    {
        var wasRunning = _playbackTimer.IsEnabled;
        _playbackTimer.Stop();
        _playbackEdges = [];
        _playbackDurationsMs = [];
        _playbackIndex = 0;
        _playbackProgress = 0;
        if (wasRunning)
        {
            PlaybackChanged?.Invoke(this, EventArgs.Empty);
            Redraw();
        }
    }

    /// <summary>Selects a node, expanding whatever hides it, and centres the view on it.</summary>
    public void BringIntoView(PlanNode node)
    {
        if (Statement is null)
        {
            return;
        }

        if (_focusRoot is not null && !_focusRoot.DescendantsAndSelf().Contains(node))
        {
            _focusRoot = null;
            RebuildLayout(preserveView: false);
            FocusChanged?.Invoke(this, EventArgs.Empty);
        }

        // Expand any collapsed ancestor, otherwise the node isn't in the layout at all.
        var path = FindPath(Statement.Root, node);
        if (path is not null)
        {
            var changed = false;
            foreach (var ancestor in path.Where(a => a != node))
            {
                changed |= _collapsed.Remove(ancestor.NodeId);
            }

            if (changed)
            {
                RebuildLayout(preserveView: true);
            }
        }

        SelectedNode = node;

        if (_layout?.Find(node) is { } nl)
        {
            _offset = new Vector2(
                (float)((_canvas.ActualWidth / 2) - (nl.CenterX * _scale)),
                (float)((_canvas.ActualHeight / 2) - ((nl.Y + (nl.Height / 2)) * _scale)));
            Redraw();
        }
    }

    #endregion

    #region Lifecycle

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshPalette();
        TryPendingFit();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        DisposeGeometry();
        _titleFormat.Dispose();
        _subtitleFormat.Dispose();
        _metaFormat.Dispose();
        _badgeFormat.Dispose();
        _expanderFormat.Dispose();

        // Win2D holds a swap chain; without this the control leaks its device resources.
        _canvas.RemoveFromVisualTree();
    }

    private void CreateTextFormats()
    {
        _titleFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Display",
            FontSize = 13.5f,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            WordWrapping = CanvasWordWrapping.NoWrap,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            TrimmingSign = CanvasTrimmingSign.Ellipsis,
            VerticalAlignment = CanvasVerticalAlignment.Top,
        };

        _subtitleFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Text",
            FontSize = 11.5f,
            WordWrapping = CanvasWordWrapping.NoWrap,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            TrimmingSign = CanvasTrimmingSign.Ellipsis,
            VerticalAlignment = CanvasVerticalAlignment.Top,
        };

        _metaFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Text",
            FontSize = 11f,
            WordWrapping = CanvasWordWrapping.NoWrap,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            TrimmingSign = CanvasTrimmingSign.Ellipsis,
            VerticalAlignment = CanvasVerticalAlignment.Top,
        };

        _badgeFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Text",
            FontSize = 10f,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            WordWrapping = CanvasWordWrapping.NoWrap,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };

        _expanderFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Text",
            FontSize = 10.5f,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            WordWrapping = CanvasWordWrapping.NoWrap,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };
    }

    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        // Rebuilt on device-lost as well as first load, hence disposing first.
        DisposeSharedGeometry();

        var node = CanvasGeometry.CreateRoundedRectangle(
            sender,
            0,
            0,
            (float)_engine.NodeWidth,
            (float)_engine.NodeHeight,
            NodeCorner,
            NodeCorner);

        var accentClip = CanvasGeometry.CreateRectangle(sender, 0, 0, AccentWidth, (float)_engine.NodeHeight);

        _nodeGeometry = node;
        _accentGeometry = node.CombineWith(accentClip, Matrix3x2.Identity, CanvasGeometryCombine.Intersect);
        accentClip.Dispose();

        using var warnPath = new CanvasPathBuilder(sender);
        warnPath.BeginFigure(6f, 0f);
        warnPath.AddLine(12f, 11f);
        warnPath.AddLine(0f, 11f);
        warnPath.EndFigure(CanvasFigureLoop.Closed);
        _warningGeometry = CanvasGeometry.CreatePath(warnPath);

        // Edge geometry is device-bound too, so drop it and let the next frame rebuild.
        DisposeEdgeGeometry();
    }

    private void DisposeGeometry()
    {
        DisposeSharedGeometry();
        DisposeEdgeGeometry();
    }

    private void DisposeSharedGeometry()
    {
        _nodeGeometry?.Dispose();
        _accentGeometry?.Dispose();
        _warningGeometry?.Dispose();
        _nodeGeometry = null;
        _accentGeometry = null;
        _warningGeometry = null;
    }

    private void DisposeEdgeGeometry()
    {
        if (_edgeGeometries is null)
        {
            return;
        }

        foreach (var g in _edgeGeometries.Values)
        {
            g.Dispose();
        }

        _edgeGeometries = null;
    }

    private void RefreshPalette()
    {
        _palette = PlanPalette.For(ActualTheme == ElementTheme.Dark);
        Redraw();
    }

    private void Redraw() => _canvas.Invalidate();

    #endregion

    #region Layout

    private void OnStatementChanged()
    {
        StopPlayback();
        _focusRoot = null;
        ClearOperatorHover();
        _collapsed.Clear();
        AddCheapSubtrees();
        ComputeHotPath();
        CollapseOutsideHotPath();
        SelectedNode = null;
        _findingCursor = -1;
        _pendingFit = true;
        _userAdjusted = false;
        RecomputeBlame();
        RecomputeVerdicts();
        ColorMode = _blame.Count > 0 ? ColorMode.Blame : ColorMode.Metric;
        RebuildLayout(preserveView: false);
        FitToView();
    }

    private Dictionary<int, Diagnostics.FindingSeverity> _blame = new();

    /// <summary>Worst finding severity touching each node, for <see cref="ColorMode.Blame"/> (Phase 4.5).</summary>
    private void RecomputeBlame()
    {
        var map = new Dictionary<int, Diagnostics.FindingSeverity>();
        if (Statement is not null)
        {
            foreach (var finding in Statement.Findings)
            {
                foreach (var node in finding.Nodes)
                {
                    if (!map.TryGetValue(node.NodeId, out var existing) || finding.Severity > existing)
                    {
                        map[node.NodeId] = finding.Severity;
                    }
                }
            }
        }

        _blame = map;
    }

    private Dictionary<int, Diagnostics.PlanFinding> _verdicts = new();

    /// <summary>
    /// Highest-ranked finding touching each node, for the verdict line (hot-path-plan.md
    /// Phase 1). <see cref="Diagnostics.RuleEngine.Analyse"/> already ranks
    /// <see cref="Diagnostics.PlanStatement.Findings"/> by severity, then impact, then
    /// confidence, so the first finding seen per node in that order is its best one.
    /// </summary>
    private void RecomputeVerdicts()
    {
        var map = new Dictionary<int, Diagnostics.PlanFinding>();
        if (Statement is not null)
        {
            foreach (var finding in Statement.Findings)
            {
                foreach (var node in finding.Nodes)
                {
                    map.TryAdd(node.NodeId, finding);
                }
            }
        }

        _verdicts = map;
    }

    private void OnMetricChanged()
    {
        RecomputeMetricMax();
        DisposeEdgeGeometry();
        Redraw();
    }

    private void RebuildLayout(bool preserveView)
    {
        DisposeEdgeGeometry();

        if (Statement is null)
        {
            _layout = null;
            Redraw();
            return;
        }

        _layout = _engine.Layout(_focusRoot ?? Statement.Root, _collapsed);
        RecomputeMetricMax();

        if (!preserveView)
        {
            CenterContent();
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
        Redraw();
    }

    private void RecomputeMetricMax()
    {
        if (Statement is null)
        {
            _metricMax = 0;
            return;
        }

        _metricMax = Metric switch
        {
            SizeMetric.SubtreeCost => Statement.MaxSubtreeCost,
            SizeMetric.OperatorCost => Statement.MaxOperatorCost,
            SizeMetric.ActualRows => Statement.MaxActualRows,
            SizeMetric.ElapsedTime => Statement.MaxElapsedMs,
            SizeMetric.SelfTime => Statement.MaxSelfTimeMs,
            SizeMetric.Efficiency => Statement.AllNodes.Count == 0 ? 0 : Statement.AllNodes.Max(EfficiencyRatio),
            SizeMetric.EstimateSkew => Statement.AllNodes.Count == 0 ? 0 : Statement.AllNodes.Max(n => n.EstimateErrorFactor ?? 0),
            _ => Statement.MaxSubtreeCost,
        };
    }

    private double MetricValue(PlanNode n) => Metric switch
    {
        SizeMetric.SubtreeCost => n.EstimatedSubtreeCost,
        SizeMetric.OperatorCost => n.EstimatedOperatorCost,
        SizeMetric.ActualRows => n.ActualRows ?? 0,
        SizeMetric.ElapsedTime => n.ActualElapsedMs ?? 0,
        SizeMetric.SelfTime => n.SelfTimeMs ?? 0,
        SizeMetric.Efficiency => EfficiencyRatio(n),
        SizeMetric.EstimateSkew => n.EstimateErrorFactor ?? 0,
        _ => n.EstimatedSubtreeCost,
    };

    /// <summary>This node's rows ÷ rows the query finally returned (Phase 4.2) — 0 when either side is unknown.</summary>
    private double EfficiencyRatio(PlanNode n)
    {
        if (Statement?.Root.ActualRows is not double finalRows || finalRows <= 0 || n.ActualRows is not double rows)
        {
            return 0;
        }

        return rows / finalRows;
    }

    /// <summary>
    /// Signed, log-scaled estimate error for the <see cref="PlanPalette.Diverging"/> ramp
    /// (Phase 4.1): positive is an underestimate, negative is an overestimate, magnitude
    /// saturates around a 100x miss so an extreme outlier doesn't wash out everything else.
    /// </summary>
    private static double EstimateSkewSignedFraction(PlanNode n)
    {
        if (n.EstimateErrorFactor is not double factor || factor <= 1)
        {
            return 0;
        }

        var magnitude = Math.Clamp(Math.Log10(factor) / 2.0, 0, 1);
        var sign = (n.ActualRows ?? 0) > n.EstimatedRowsTotal ? 1 : -1;
        return sign * magnitude;
    }

    private double MetricFraction(PlanNode n) => _metricMax <= 0 ? 0 : MetricValue(n) / _metricMax;

    private void CenterContent()
    {
        if (_layout is null)
        {
            return;
        }

        _offset = new Vector2(
            (float)((_canvas.ActualWidth - (_layout.Width * _scale)) / 2),
            (float)Math.Max(0, (_canvas.ActualHeight - (_layout.Height * _scale)) / 2));
    }

    private static IReadOnlyList<PlanNode>? FindPath(PlanNode from, PlanNode target)
    {
        if (ReferenceEquals(from, target))
        {
            return [from];
        }

        foreach (var child in from.Children)
        {
            if (FindPath(child, target) is { } sub)
            {
                return new[] { from }.Concat(sub).ToList();
            }
        }

        return null;
    }

    #endregion

    #region Drawing

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;

        if (_layout is null || _nodeGeometry is null)
        {
            return;
        }

        var view = Matrix3x2.CreateScale(_scale) * Matrix3x2.CreateTranslation(_offset);

        // §8.2 — the visible rect in plan coordinates, plus a buffer so a node scrolling in
        // is already drawn. Everything outside it is skipped before any drawing work.
        if (!Matrix3x2.Invert(view, out var inverse))
        {
            return;
        }

        var topLeft = Vector2.Transform(new Vector2(0, 0), inverse);
        var bottomRight = Vector2.Transform(
            new Vector2((float)sender.ActualWidth, (float)sender.ActualHeight),
            inverse);

        const float buffer = 240f;
        var left = topLeft.X - buffer;
        var top = topLeft.Y - buffer;
        var right = bottomRight.X + buffer;
        var bottom = bottomRight.Y + buffer;

        var drawText = _scale >= LodTextThreshold;
        var filter = FilterText?.Trim() ?? string.Empty;
        var filtering = filter.Length > 0;

        _edgeGeometries ??= BuildEdgeGeometry(sender);

        ds.Transform = view;
        ds.Antialiasing = CanvasAntialiasing.Antialiased;

        foreach (var edge in _layout.Edges)
        {
            if (!EdgeIntersects(edge, left, top, right, bottom))
            {
                continue;
            }

            if (!_edgeGeometries.TryGetValue(edge, out var geo))
            {
                continue;
            }

            var thickness = EdgeThickness(edge);
            var crossesParallelismBoundary = edge.Parent.Node.Parallel != edge.Child.Node.Parallel;
            var isHotPath = _hotPathNodeIds.Contains(edge.Parent.Node.NodeId)
                         && _hotPathNodeIds.Contains(edge.Child.Node.NodeId);
            var color = isHotPath
                ? _palette.Selection
                : crossesParallelismBoundary
                ? _palette.Caution
                : edge.IsActual ? _palette.Edge : _palette.EdgeEstimated;
            if (filtering)
            {
                color = _palette.Fade(color, 0.35);
            }

            // Wrong-by-10x+ estimate through this edge (Phase 4.6): the flow itself shows
            // the bad guess, not just the node it lands on.
            if (edge.Child.Node.HasBadEstimate)
            {
                ds.DrawGeometry(geo, color, thickness, DashedStroke);
            }
            else
            {
                ds.DrawGeometry(geo, color, thickness);
            }
        }

        DrawPlayback(ds);

        foreach (var nl in _layout.Nodes)
        {
            if (!nl.IntersectsViewport(left, top, right, bottom))
            {
                continue;
            }

            var dimmed = filtering && !Matches(nl.Node, filter);
            ds.Transform = Matrix3x2.CreateTranslation((float)nl.X, (float)nl.Y) * view;
            DrawNode(ds, nl, dimmed, drawText);
        }

        DrawRemovedDiffNodes(ds, view);

        ds.Transform = Matrix3x2.Identity;
        DrawMinimap(ds, sender, left, top, right, bottom);
    }

    private void DrawRemovedDiffNodes(CanvasDrawingSession ds, Matrix3x2 view)
    {
        if (_diff is null || _diffBeforeLayout is null || _nodeGeometry is null)
        {
            return;
        }

        var removed = _diff.Nodes.Where(delta => delta.Kind == PlanDiffKind.Removed && delta.Before is not null);
        foreach (var delta in removed)
        {
            if (_diffBeforeLayout.Find(delta.Before!) is not { } oldLayout)
            {
                continue;
            }

            ds.Transform = Matrix3x2.CreateTranslation((float)oldLayout.X, (float)oldLayout.Y) * view;
            ds.FillGeometry(_nodeGeometry, _palette.WithAlpha(_palette.Danger, 45));
            ds.DrawGeometry(_nodeGeometry, _palette.Danger, Math.Max(1f, 2f / _scale), DashedStroke);
        }
    }

    private void DrawMinimap(
        CanvasDrawingSession ds,
        CanvasControl sender,
        double viewportLeft,
        double viewportTop,
        double viewportRight,
        double viewportBottom)
    {
        if (_layout is null || sender.ActualWidth < 260 || sender.ActualHeight < 180)
        {
            _minimapBounds = Rect.Empty;
            return;
        }

        const float width = 180f;
        const float height = 118f;
        const float margin = 14f;
        const float padding = 7f;
        _minimapBounds = new Rect(sender.ActualWidth - width - margin, sender.ActualHeight - height - margin, width, height);

        ds.FillRectangle(_minimapBounds, _palette.WithAlpha(_palette.Surface, 235));
        ds.DrawRectangle(_minimapBounds, _palette.NodeBorder, 1f);

        _minimapScale = (float)Math.Min(
            (width - (padding * 2)) / _layout.Width,
            (height - (padding * 2)) / _layout.Height);
        var contentWidth = (float)_layout.Width * _minimapScale;
        var contentHeight = (float)_layout.Height * _minimapScale;
        _minimapPlanOrigin = new Vector2(
            (float)_minimapBounds.X + ((width - contentWidth) / 2),
            (float)_minimapBounds.Y + ((height - contentHeight) / 2));

        Vector2 Map(double x, double y) => _minimapPlanOrigin + new Vector2((float)x, (float)y) * _minimapScale;

        foreach (var edge in _layout.Edges)
        {
            ds.DrawLine(Map(edge.Parent.CenterX, edge.Parent.Bottom), Map(edge.Child.CenterX, edge.Child.Y), _palette.EdgeEstimated, 0.7f);
        }

        foreach (var node in _layout.Nodes)
        {
            var position = Map(node.X, node.Y);
            var nodeWidth = Math.Max(3f, (float)node.Width * _minimapScale);
            var nodeHeight = Math.Max(2f, (float)node.Height * _minimapScale);
            ds.FillRectangle(position.X, position.Y, nodeWidth, nodeHeight, MinimapHeat(node.Node));
        }

        var viewStart = Map(Math.Clamp(viewportLeft, 0, _layout.Width), Math.Clamp(viewportTop, 0, _layout.Height));
        var viewEnd = Map(Math.Clamp(viewportRight, 0, _layout.Width), Math.Clamp(viewportBottom, 0, _layout.Height));
        ds.DrawRectangle(
            viewStart.X,
            viewStart.Y,
            Math.Max(2, viewEnd.X - viewStart.X),
            Math.Max(2, viewEnd.Y - viewStart.Y),
            _palette.Selection,
            1.5f);
    }

    private Color MinimapHeat(PlanNode node)
    {
        if (ColorMode == ColorMode.Blame)
        {
            return _blame.TryGetValue(node.NodeId, out var severity)
                ? _palette.FindingAccent(severity)
                : _palette.NodeBorder;
        }

        return Metric == SizeMetric.EstimateSkew
            ? _palette.Diverging(EstimateSkewSignedFraction(node))
            : _palette.Heat(MetricFraction(node));
    }

    private void DrawPlayback(CanvasDrawingSession ds)
    {
        if (!IsPlaybackRunning || _playbackIndex >= _playbackEdges.Count)
        {
            return;
        }

        var edge = _playbackEdges[_playbackIndex];
        if (_edgeGeometries?.TryGetValue(edge, out var geometry) == true)
        {
            ds.DrawGeometry(geometry, _palette.Selection, Math.Max(3.5f, EdgeThickness(edge) + 1.5f));
        }

        var from = new Vector2((float)edge.Child.CenterX, (float)edge.Child.Y);
        var to = new Vector2((float)edge.Parent.CenterX, (float)edge.Parent.Bottom);
        var dy = Math.Max(24f, (from.Y - to.Y) * 0.55f);
        var c1 = new Vector2(from.X, from.Y - dy);
        var c2 = new Vector2(to.X, to.Y + dy);
        var position = CubicBezier(from, c1, c2, to, (float)_playbackProgress);
        ds.FillCircle(position, 6f, _palette.Selection);
        ds.DrawCircle(position, 6f, _palette.NodeBase, 1.5f);
    }

    private static Vector2 CubicBezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        var u = 1 - t;
        return (u * u * u * p0)
             + (3 * u * u * t * p1)
             + (3 * u * t * t * p2)
             + (t * t * t * p3);
    }

    private static IReadOnlyList<EdgeLayout> ExecutionOrderEdges(PlanLayout layout)
    {
        var byParent = layout.Edges
            .GroupBy(e => e.Parent.Node)
            .ToDictionary(g => g.Key, g => g.ToList());
        var ordered = new List<EdgeLayout>(layout.Edges.Count);

        void Visit(PlanNode node)
        {
            if (!byParent.TryGetValue(node, out var edges))
            {
                return;
            }

            foreach (var edge in edges)
            {
                Visit(edge.Child.Node);
                ordered.Add(edge);
            }
        }

        var root = layout.Nodes.MinBy(n => n.Depth)?.Node;
        if (root is not null)
        {
            Visit(root);
        }

        return ordered;
    }

    private void OnPlaybackTick(object? sender, object e)
    {
        if (_playbackIndex >= _playbackDurationsMs.Count)
        {
            StopPlayback();
            return;
        }

        var elapsed = (DateTime.UtcNow - _playbackStepStartedUtc).TotalMilliseconds;
        var duration = _playbackDurationsMs[_playbackIndex];
        if (elapsed >= duration)
        {
            _playbackIndex++;
            _playbackStepStartedUtc = DateTime.UtcNow;
            _playbackProgress = 0;
            if (_playbackIndex >= _playbackEdges.Count)
            {
                StopPlayback();
                return;
            }
        }
        else
        {
            _playbackProgress = elapsed / duration;
        }

        Redraw();
    }

    private Dictionary<EdgeLayout, CanvasGeometry> BuildEdgeGeometry(ICanvasResourceCreator creator)
    {
        var map = new Dictionary<EdgeLayout, CanvasGeometry>(_layout!.Edges.Count);
        foreach (var edge in _layout.Edges)
        {
            var x0 = (float)edge.Parent.CenterX;
            var y0 = (float)edge.Parent.Bottom;
            var x1 = (float)edge.Child.CenterX;
            var y1 = (float)edge.Child.Y;
            var dy = Math.Max(24f, (y1 - y0) * 0.55f);

            using var path = new CanvasPathBuilder(creator);
            path.BeginFigure(x0, y0);
            path.AddCubicBezier(new Vector2(x0, y0 + dy), new Vector2(x1, y1 - dy), new Vector2(x1, y1));
            path.EndFigure(CanvasFigureLoop.Open);
            map[edge] = CanvasGeometry.CreatePath(path);
        }

        return map;
    }

    private float EdgeThickness(EdgeLayout edge)
    {
        if (_layout is null || _layout.MaxEdgeRows <= 0)
        {
            return 1.4f;
        }

        // Row counts span orders of magnitude, so a linear map would render every edge but
        // the fattest as a hairline. Log keeps the whole range legible.
        var t = Math.Log(1 + Math.Max(0, edge.Rows)) / Math.Log(1 + _layout.MaxEdgeRows);
        return (float)(1.3 + (7.0 * Math.Clamp(t, 0, 1)));
    }

    private static bool EdgeIntersects(EdgeLayout e, double left, double top, double right, double bottom)
    {
        var minX = Math.Min(e.Parent.CenterX, e.Child.CenterX);
        var maxX = Math.Max(e.Parent.CenterX, e.Child.CenterX);
        return minX <= right && maxX >= left && e.Parent.Bottom <= bottom && e.Child.Y >= top;
    }

    private bool Matches(PlanNode node, string filter) =>
        node.PhysicalOp.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || node.LogicalOp.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (node.ObjectName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || (node.Predicate?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || (Diagnostics.NodeLabeller.DescribeSources(node)?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || node.OutputList.Any(o => o.Contains(filter, StringComparison.OrdinalIgnoreCase))
        || node.Warnings.Any(w => w.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase));

    /// <summary>The more restrictive of the user's <see cref="LabelDetail"/> and the zoom-driven density (hot-path-plan.md Phase 1).</summary>
    private Controls.LabelDetail EffectiveDetail()
    {
        if (_scale < LodDetailThreshold || LabelDetail == Controls.LabelDetail.Minimal)
        {
            return Controls.LabelDetail.Minimal;
        }

        return LabelDetail == Controls.LabelDetail.Full ? Controls.LabelDetail.Full : Controls.LabelDetail.Standard;
    }

    /// <summary>
    /// Node subtitle in order of preference: this node's own object, a derived source
    /// description (join sides plus keys, when parseable), then the logical op as a last
    /// resort (hot-path-plan.md Phase 1). Long object names truncate from the left so the
    /// distinguishing suffix survives.
    /// </summary>
    private static string? DescribeSubtitle(PlanNode node)
    {
        if (!string.IsNullOrEmpty(node.ObjectName))
        {
            return Diagnostics.NodeLabeller.TruncateObjectName(node.ObjectName);
        }

        var sources = Diagnostics.NodeLabeller.DescribeSources(node);
        if (sources is not null)
        {
            var joinKeys = Diagnostics.NodeLabeller.DescribeJoinKeys(node);
            return joinKeys is null ? sources : $"{sources} {joinKeys}";
        }

        return string.IsNullOrEmpty(node.LogicalOp) || node.LogicalOp == node.PhysicalOp
            ? null
            : node.LogicalOp;
    }

    private void DrawNode(CanvasDrawingSession ds, NodeLayout nl, bool dimmed, bool drawText)
    {
        var node = nl.Node;
        var w = (float)nl.Width;
        var h = (float)nl.Height;
        var hairline = 1f / _scale;
        var fraction = MetricFraction(node);
        var isSelected = ReferenceEquals(SelectedNode, node);

        Diagnostics.FindingSeverity? blameSeverity = ColorMode == ColorMode.Blame && _blame.TryGetValue(node.NodeId, out var bs)
            ? bs
            : null;
        var blameUnimplicated = ColorMode == ColorMode.Blame && blameSeverity is null;

        var fill = _palette.NodeFill(fraction, dimmed || blameUnimplicated);
        var heat = ColorMode == ColorMode.Blame
            ? (blameSeverity is { } sev ? _palette.FindingAccent(sev) : _palette.Fade(_palette.NodeBorder, 0.5))
            : Metric == SizeMetric.EstimateSkew
                ? _palette.Diverging(EstimateSkewSignedFraction(node))
                : _palette.Heat(fraction);
        if (_diffKinds.TryGetValue(node, out var diffKind))
        {
            heat = diffKind switch
            {
                PlanDiffKind.Added => DiffAddedColor,
                PlanDiffKind.Changed => _palette.Caution,
                _ => heat,
            };
        }
        if (dimmed)
        {
            heat = _palette.Fade(heat, 0.4);
        }

        ds.FillGeometry(_nodeGeometry, fill);
        ds.FillGeometry(_accentGeometry, heat);

        // Redundant encoding (Phase 4.7): colour alone fails for ~8% of men and dies in
        // greyscale, so the accent strip's width also tracks the same signal — mask the
        // unused portion with the node's own fill colour rather than rebuilding geometry.
        var accentT = ColorMode == ColorMode.Blame
            ? blameSeverity switch
            {
                Diagnostics.FindingSeverity.Critical => 1.0,
                Diagnostics.FindingSeverity.Warning => 0.7,
                Diagnostics.FindingSeverity.Info => 0.5,
                _ => 0.3,
            }
            : Math.Clamp(fraction, 0, 1);
        var accentW = AccentWidth * (float)(0.3 + (0.7 * accentT));
        if (accentW < AccentWidth - 0.5f)
        {
            ds.FillRectangle(accentW, 0, AccentWidth - accentW, h, fill);
        }

        var border = node.WorstWarning switch
        {
            WarningSeverity.Critical => _palette.Danger,
            WarningSeverity.Warning => _palette.Caution,
            _ => _palette.NodeBorder,
        };

        if (dimmed)
        {
            border = _palette.Fade(border, 0.4);
        }

        ds.DrawGeometry(_nodeGeometry, border, hairline * (node.WorstWarning is null ? 1f : 1.5f));

        if (_hotPathNodeIds.Contains(node.NodeId))
        {
            ds.DrawGeometry(_nodeGeometry, _palette.Selection, hairline * 2f);
        }

        if (isSelected)
        {
            ds.DrawGeometry(_nodeGeometry, _palette.WithAlpha(_palette.Selection, 70), hairline * 6f);
            ds.DrawGeometry(_nodeGeometry, _palette.Selection, hairline * 2f);
        }

        if (!drawText)
        {
            // §8.7 — below the LOD threshold the text would be unreadable, so skip it and
            // let colour alone carry the shape of the plan.
            return;
        }

        var textLeft = TextLeftPad;
        var textWidth = w - TextLeftPad - TextRightPad;

        var primary = dimmed ? _palette.Fade(_palette.TextPrimary, 0.45) : _palette.TextPrimary;
        var secondary = dimmed ? _palette.Fade(_palette.TextSecondary, 0.45) : _palette.TextSecondary;
        var tertiary = dimmed ? _palette.Fade(_palette.TextTertiary, 0.45) : _palette.TextTertiary;

        var detail = EffectiveDetail();

        if (detail == Controls.LabelDetail.Minimal)
        {
            // §8.7 companion tier: still zoomed in enough to read text, but the card reads
            // as operator name plus heat only — no subtitle, badges, or metric lines.
            ds.DrawText(node.PhysicalOp, new Rect(textLeft, 9, Math.Max(10, textWidth), 18), primary, _titleFormat);
            return;
        }

        // Reserve room on the title row for the severity glyph and the parallel marker.
        // Phase 4.7: the glyph fires on either a Showplan warning or a blame finding, so a
        // node the rule engine flagged is visibly marked even without a plan warning.
        var showSeverityGlyph = node.Warnings.Count > 0 || blameSeverity is not null;
        var badgeRoom = 0f;
        if (showSeverityGlyph)
        {
            badgeRoom += 18f;
        }

        if (node.Parallel)
        {
            badgeRoom += 14f;
        }

        ds.DrawText(
            node.PhysicalOp,
            new Rect(textLeft, 9, Math.Max(10, textWidth - badgeRoom), 18),
            primary,
            _titleFormat);

        var cursorY = 28f;

        var subtitle = DescribeSubtitle(node);
        if (!string.IsNullOrEmpty(subtitle))
        {
            ds.DrawText(subtitle, new Rect(textLeft, cursorY, textWidth, 16), secondary, _subtitleFormat);
            cursorY += 19f;
        }

        if (detail == Controls.LabelDetail.Full
            && _verdicts.TryGetValue(node.NodeId, out var verdict))
        {
            ds.DrawLine(textLeft, cursorY, textLeft + textWidth, cursorY, _palette.Fade(tertiary, 0.5f), hairline);
            cursorY += 5f;
            ds.DrawText(
                verdict.Title,
                new Rect(textLeft, cursorY, textWidth, 15),
                dimmed ? _palette.Fade(_palette.FindingAccent(verdict.Severity), 0.45) : _palette.FindingAccent(verdict.Severity),
                _metaFormat);
            cursorY += 18f;
        }

        cursorY = DrawRowsLine(ds, node, textLeft, textWidth, cursorY, secondary, tertiary, dimmed);

        if (node.HasRuntimeStats
            && Statement?.Summary.QueryElapsedMs is double queryElapsedMs
            && queryElapsedMs > 0)
        {
            var share = Math.Clamp((node.SelfTimeMs ?? 0) / queryElapsedMs, 0, 1);
            ds.DrawText(
                $"self {Format.Milliseconds(node.SelfTimeMs ?? 0)} · {Format.Percent(share)} of query",
                new Rect(textLeft, cursorY, textWidth, 15),
                tertiary,
                _metaFormat);
            cursorY += 17f;
        }

        DrawCostLine(ds, node, textLeft, textWidth, cursorY, tertiary);

        var badgeX = w - TextRightPad;
        if (node.Parallel)
        {
            badgeX -= 10f;
            DrawParallelMarker(ds, badgeX, 12f, tertiary);
        }

        if (showSeverityGlyph && _warningGeometry is not null)
        {
            badgeX -= 16f;
            var warnColor = node.Warnings.Count > 0
                ? (node.WorstWarning == WarningSeverity.Critical ? _palette.Danger : _palette.Caution)
                : _palette.FindingAccent(blameSeverity!.Value);
            ds.Transform = Matrix3x2.CreateTranslation(badgeX, 10f) * ds.Transform;
            ds.FillGeometry(_warningGeometry, dimmed ? _palette.Fade(warnColor, 0.4) : warnColor);
            ds.Transform = Matrix3x2.CreateTranslation(-badgeX, -10f) * ds.Transform;
        }

        if (nl.HasChildren)
        {
            DrawExpander(ds, nl, w, h, hairline, dimmed);
        }
    }

    private float DrawRowsLine(
        CanvasDrawingSession ds,
        PlanNode node,
        float textLeft,
        float textWidth,
        float y,
        Color secondary,
        Color tertiary,
        bool dimmed)
    {
        string rowsText;
        var color = secondary;

        if (node.ActualRows is double actual)
        {
            rowsText = $"{Format.Rows(actual)} rows · est {Format.Rows(node.EstimatedRowsTotal)}";
            if (node.HasBadEstimate)
            {
                // The whole point of the tool (TDD §8): a bad estimate should be visible
                // without clicking anything.
                color = dimmed ? _palette.Fade(_palette.Danger, 0.45) : _palette.Danger;
                rowsText = $"{Format.Rows(actual)} rows · {Format.Factor(node.EstimateErrorFactor!.Value)} off";
            }
        }
        else
        {
            rowsText = $"est {Format.Rows(node.EstimatedRowsTotal)} rows";
            color = tertiary;
        }

        ds.DrawText(rowsText, new Rect(textLeft, y, textWidth, 15), color, _metaFormat);
        return y + 17f;
    }

    private void DrawCostLine(
        CanvasDrawingSession ds,
        PlanNode node,
        float textLeft,
        float textWidth,
        float y,
        Color tertiary)
    {
        var share = _metricMax <= 0 ? 0 : MetricValue(node) / _metricMax;
        var text = Metric switch
        {
            SizeMetric.ElapsedTime when node.ActualElapsedMs is double ms =>
                $"{Format.Milliseconds(ms)} · {Format.Percent(share)}",
            SizeMetric.ActualRows =>
                $"{Format.Percent(share)} of widest flow",
            SizeMetric.OperatorCost =>
                $"op cost {Format.Cost(node.EstimatedOperatorCost)} · {Format.Percent(share)}",
            SizeMetric.SelfTime when node.SelfTimeMs is double self =>
                $"self {Format.Milliseconds(self)} · {Format.Percent(share)}",
            SizeMetric.Efficiency =>
                $"{MetricValue(node):0.#}x rows read vs. final output",
            SizeMetric.EstimateSkew when node.EstimateErrorFactor is double f =>
                $"{Format.Factor(f)} {((node.ActualRows ?? 0) > node.EstimatedRowsTotal ? "under" : "over")}",
            _ =>
                $"subtree {Format.Cost(node.EstimatedSubtreeCost)} · {Format.Percent(share)}",
        };

        ds.DrawText(text, new Rect(textLeft, y, textWidth, 15), tertiary, _metaFormat);
    }

    private void DrawParallelMarker(CanvasDrawingSession ds, float x, float y, Color color)
    {
        var hairline = Math.Max(1f, 1.4f / _scale);
        ds.DrawLine(x, y, x, y + 9f, color, hairline);
        ds.DrawLine(x + 4f, y, x + 4f, y + 9f, color, hairline);
    }

    private void DrawExpander(CanvasDrawingSession ds, NodeLayout nl, float w, float h, float hairline, bool dimmed)
    {
        var cx = w / 2f;
        var cy = h;
        const float r = 9.5f;

        var fill = dimmed ? _palette.Fade(_palette.NodeBase, 0.4) : _palette.NodeBase;
        var stroke = dimmed ? _palette.Fade(_palette.NodeBorder, 0.4) : _palette.NodeBorder;
        var text = dimmed ? _palette.Fade(_palette.TextSecondary, 0.4) : _palette.TextSecondary;

        ds.FillCircle(cx, cy, r, fill);
        ds.DrawCircle(cx, cy, r, stroke, hairline);

        var label = nl.IsCollapsed ? $"+{nl.HiddenDescendantCount}" : "–";
        ds.DrawText(
            label,
            new Rect(cx - r, cy - r, r * 2, r * 2),
            nl.IsCollapsed ? _palette.Selection : text,
            _expanderFormat);
    }

    #endregion

    #region Input

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ClearOperatorHover();
        Focus(FocusState.Pointer);
        _pointerDown = true;
        _dragDistance = 0;
        _dragStart = e.GetCurrentPoint(_canvas).Position;
        _dragOffsetStart = _offset;
        _canvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown)
        {
            UpdateOperatorHover(e.GetCurrentPoint(_canvas).Position);
            return;
        }

        var p = e.GetCurrentPoint(_canvas).Position;
        var dx = p.X - _dragStart.X;
        var dy = p.Y - _dragStart.Y;
        _dragDistance = Math.Max(_dragDistance, Math.Abs(dx) + Math.Abs(dy));

        _offset = _dragOffsetStart + new Vector2((float)dx, (float)dy);
        _userAdjusted = true;
        Redraw();
        e.Handled = true;
    }

    private static Color DiffAddedColor => Color.FromArgb(255, 16, 124, 72);

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => ClearOperatorHover();

    private void UpdateOperatorHover(Point position)
    {
        var planPoint = ToPlanCoordinates(position);
        var node = _layout?.HitTest(planPoint.X, planPoint.Y)?.Node;
        if (ReferenceEquals(node, _hoveredNode))
        {
            return;
        }

        _operatorToolTip.IsOpen = false;
        _hoveredNode = node;

        if (node is null)
        {
            _operatorToolTip.IsEnabled = false;
            return;
        }

        var explanation = OperatorGlossary.Explain(node, ExplanationVerbosity);
        _operatorToolTip.Content = BuildOperatorCard(explanation);
        _operatorToolTip.IsEnabled = true;
    }

    private static UIElement BuildOperatorCard(OperatorExplanation explanation)
    {
        var card = new StackPanel { Spacing = 7 };
        card.Children.Add(new TextBlock
        {
            Text = explanation.Title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        card.Children.Add(new TextBlock
        {
            Text = explanation.Description,
            MaxWidth = 350,
            TextWrapping = TextWrapping.Wrap,
        });
        card.Children.Add(new TextBlock
        {
            Text = explanation.Evidence,
            MaxWidth = 350,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
        });
        return card;
    }

    private void ClearOperatorHover()
    {
        _hoveredNode = null;
        _operatorToolTip.IsOpen = false;
        _operatorToolTip.IsEnabled = false;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown)
        {
            return;
        }

        _pointerDown = false;
        _canvas.ReleasePointerCapture(e.Pointer);

        // A click is a press-and-release that didn't travel — anything further was a pan.
        if (_dragDistance < 4)
        {
            HandleClick(e.GetCurrentPoint(_canvas).Position);
        }

        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) => _pointerDown = false;

    private void HandleClick(Point position)
    {
        if (_layout is null)
        {
            return;
        }

        if (HandleMinimapClick(position))
        {
            return;
        }

        var p = ToPlanCoordinates(position);

        // The expander sits on the node's bottom edge, so it has to win the hit test.
        foreach (var nl in _layout.Nodes)
        {
            if (!nl.HasChildren)
            {
                continue;
            }

            var dx = p.X - nl.CenterX;
            var dy = p.Y - nl.Bottom;
            if ((dx * dx) + (dy * dy) <= 13 * 13)
            {
                ToggleCollapse(nl.Node);
                return;
            }
        }

        SelectedNode = _layout.HitTest(p.X, p.Y)?.Node;
    }

    private bool HandleMinimapClick(Point position)
    {
        if (_layout is null
            || _minimapBounds.IsEmpty
            || position.X < _minimapBounds.X
            || position.X > _minimapBounds.Right
            || position.Y < _minimapBounds.Y
            || position.Y > _minimapBounds.Bottom
            || _minimapScale <= 0)
        {
            return false;
        }

        var planPoint = (new Vector2((float)position.X, (float)position.Y) - _minimapPlanOrigin) / _minimapScale;
        _offset = new Vector2(
            (float)(_canvas.ActualWidth / 2) - (planPoint.X * _scale),
            (float)(_canvas.ActualHeight / 2) - (planPoint.Y * _scale));
        _userAdjusted = true;
        Redraw();
        return true;
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_layout is null)
        {
            return;
        }

        var p = ToPlanCoordinates(e.GetPosition(_canvas));
        if (_layout.HitTest(p.X, p.Y)?.Node is { } node)
        {
            if (ReferenceEquals(node, _focusRoot))
            {
                ExitFocus();
            }
            else
            {
                FocusOnSubtree(node);
            }
        }

        e.Handled = true;
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_canvas);
        var delta = point.Properties.MouseWheelDelta;

        // Ctrl+wheel and bare wheel both zoom — this canvas has nothing else to scroll.
        var factor = (float)Math.Pow(1.0015, delta);
        ZoomAbout(factor, point.Position);
        e.Handled = true;
    }

    private void ZoomAbout(float factor, Point center)
    {
        var newScale = Math.Clamp(_scale * factor, MinScale, MaxScale);
        if (Math.Abs(newScale - _scale) < float.Epsilon)
        {
            return;
        }

        // Keep the plan point under the cursor pinned while the scale changes.
        var before = ToPlanCoordinates(center);
        _scale = newScale;
        _userAdjusted = true;
        _offset = new Vector2(
            (float)(center.X - (before.X * _scale)),
            (float)(center.Y - (before.Y * _scale)));

        Redraw();
    }

    private Point ToPlanCoordinates(Point screen) => new(
        (screen.X - _offset.X) / _scale,
        (screen.Y - _offset.Y) / _scale);

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Add:
            case (VirtualKey)187: // '=' / '+' on the main row
                ZoomBy(1.2f);
                break;
            case VirtualKey.Subtract:
            case (VirtualKey)189: // '-' on the main row
                ZoomBy(1 / 1.2f);
                break;
            case VirtualKey.Number0:
                FitToView();
                break;
            case VirtualKey.Home:
                ResetZoom();
                break;
            case VirtualKey.Left:
                MoveSelection(horizontalOffset: -1, verticalOffset: 0);
                break;
            case VirtualKey.Right:
                MoveSelection(horizontalOffset: 1, verticalOffset: 0);
                break;
            case VirtualKey.Up:
                MoveSelection(horizontalOffset: 0, verticalOffset: -1);
                break;
            case VirtualKey.Down:
                MoveSelection(horizontalOffset: 0, verticalOffset: 1);
                break;
            case VirtualKey.N:
                MoveFinding(1);
                break;
            case VirtualKey.P:
                MoveFinding(-1);
                break;
            case VirtualKey.F:
                FitToView();
                break;
            case (VirtualKey)191: // '/'
                SearchRequested?.Invoke(this, EventArgs.Empty);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void MoveSelection(int horizontalOffset, int verticalOffset)
    {
        var root = _focusRoot ?? Statement?.Root;
        if (root is null)
        {
            return;
        }

        var current = SelectedNode ?? root;
        PlanNode? target = current;

        if (verticalOffset < 0)
        {
            target = FindParent(root, current) ?? current;
        }
        else if (verticalOffset > 0)
        {
            target = current.Children.FirstOrDefault() ?? current;
        }
        else if (horizontalOffset != 0 && FindParent(root, current) is { } parent)
        {
            var index = Enumerable.Range(0, parent.Children.Count)
                .FirstOrDefault(i => ReferenceEquals(parent.Children[i], current));
            var next = Math.Clamp(index + horizontalOffset, 0, parent.Children.Count - 1);
            target = parent.Children[next];
        }

        BringIntoView(target);
    }

    private void MoveFinding(int direction)
    {
        if (Statement?.Findings.SelectMany(f => f.Nodes).Distinct().ToList() is not { Count: > 0 } nodes)
        {
            return;
        }

        _findingCursor = (_findingCursor + direction + nodes.Count) % nodes.Count;
        BringIntoView(nodes[_findingCursor]);
    }

    private static PlanNode? FindParent(PlanNode root, PlanNode target)
    {
        foreach (var child in root.Children)
        {
            if (ReferenceEquals(child, target))
            {
                return root;
            }

            if (FindParent(child, target) is { } parent)
            {
                return parent;
            }
        }

        return null;
    }

    #endregion
}
