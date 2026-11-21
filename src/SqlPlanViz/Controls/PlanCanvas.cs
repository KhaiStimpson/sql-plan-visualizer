using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
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
    /// <summary>The coloured left edge of a node card (crit / warn / ok): 3px.</summary>
    private const float AccentWidth = 3f;

    /// <summary>Card corners: Gauge r-3.</summary>
    private const float NodeCorner = 4f;

    /// <summary>Card padding, as the mock-up's 7px 10px 8px 12px.</summary>
    private const float TextLeftPad = 12f;
    private const float TextRightPad = 10f;
    private const float TextTopPad = 7f;

    /// <summary>Collapse handle: a 16px bg-3 circle with a line-ctl border.</summary>
    private const float HandleRadius = 8f;
    private const float LodTextThreshold = 0.45f;

    /// <summary>Below this zoom, nodes show the operator name only (hot-path-plan.md Phase 1) — above §8.7's all-text-off threshold but too dense for the full card.</summary>
    private const float LodDetailThreshold = 0.75f;
    private const float MinScale = 0.05f;
    private const float MaxScale = 3.0f;

    private static readonly CanvasStrokeStyle DashedStroke = new() { CustomDashStyle = [2f, 1.6f] };

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
    private Dictionary<PlanNode, PlanNodeDelta> _diffDeltas = [];
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
        _diffDeltas = diff?.Nodes
            .Where(delta => delta.After is not null)
            .ToDictionary(delta => delta.After!)
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
        CanvasTextFormat Format(string family, float size, bool semibold = false, CanvasHorizontalAlignment align = CanvasHorizontalAlignment.Left) => new()
        {
            FontFamily = family,
            FontSize = size,
            FontWeight = semibold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            WordWrapping = CanvasWordWrapping.NoWrap,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            TrimmingSign = CanvasTrimmingSign.Ellipsis,
            HorizontalAlignment = align,
            VerticalAlignment = CanvasVerticalAlignment.Top,
        };

        // Operator name: 13px 600 fg-0.
        _titleFormat = Format(Themes.GaugeFonts.SansSingle, 13f, semibold: true);

        // Object line: 11.5px mono in the code role.
        _subtitleFormat = Format(Themes.GaugeFonts.MonoSingle, 11.5f);

        // Headline and facts: 11.5px sans.
        _metaFormat = Format(Themes.GaugeFonts.SansSingle, 11.5f);

        // Severity word, top-right: 11.5px 600.
        _badgeFormat = Format(Themes.GaugeFonts.SansSingle, 11.5f, semibold: true, CanvasHorizontalAlignment.Right);

        // Collapse handle glyph and edge row labels.
        _expanderFormat = Format(Themes.GaugeFonts.SansSingle, 12f, align: CanvasHorizontalAlignment.Center);
        _expanderFormat.VerticalAlignment = CanvasVerticalAlignment.Center;
        _edgeLabelFormat = Format(Themes.GaugeFonts.MonoSingle, 11f);
        _legendFormat = Format(Themes.GaugeFonts.SansSingle, 11.5f);
        _legendFormat.VerticalAlignment = CanvasVerticalAlignment.Center;
    }

    private CanvasTextFormat? _edgeLabelFormat;
    private CanvasTextFormat? _legendFormat;

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
        _gridBrush?.Dispose();
        _gridTile?.Dispose();
        _gridBrush = null;
        _gridTile = null;
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

        DrawGrid(ds, sender);

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

            // Neutral line-ctl edges whose width carries rows; the hot path is the accent and
            // a step thicker. A parallelism boundary is a fact, not a severity, so it stays
            // neutral too (fg-3) rather than borrowing the warning colour.
            var isHotPath = _hotPathNodeIds.Contains(edge.Parent.Node.NodeId)
                         && _hotPathNodeIds.Contains(edge.Child.Node.NodeId);
            var thickness = EdgeThickness(edge) + (isHotPath ? 1.5f : 0f);
            var crossesParallelismBoundary = edge.Parent.Node.Parallel != edge.Child.Node.Parallel;
            var color = isHotPath
                ? _palette.Selection
                : crossesParallelismBoundary
                ? _palette.TextTertiary
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

        if (drawText && _edgeLabelFormat is not null)
        {
            DrawEdgeLabels(ds, left, top, right, bottom, filtering);
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
        DrawLegend(ds, sender);
    }

    /// <summary>Row counts beside each edge, where it meets its child: 11px mono fg-2.</summary>
    private void DrawEdgeLabels(CanvasDrawingSession ds, double left, double top, double right, double bottom, bool filtering)
    {
        var color = filtering ? _palette.Fade(_palette.TextSecondary, 0.35) : _palette.TextSecondary;
        foreach (var edge in _layout!.Edges)
        {
            if (!EdgeIntersects(edge, left, top, right, bottom) || edge.Rows <= 0)
            {
                continue;
            }

            var text = $"{edge.Rows:N0} rows";
            using var layout = new CanvasTextLayout(ds, text, _edgeLabelFormat, 200, 16);
            var width = (float)layout.LayoutBounds.Width;
            var x2 = (float)edge.Child.CenterX;
            var x1 = (float)edge.Parent.CenterX;
            var x = x2 >= x1 - 0.5f ? x2 + 10f : x2 - 10f - width;
            var y = (float)edge.Child.Y - 22f;
            ds.DrawTextLayout(layout, x, y, color);
        }
    }

    /// <summary>The legend chip, bottom-left: Hot path / Rows (width) / Estimate off ≥10×.</summary>
    private void DrawLegend(CanvasDrawingSession ds, CanvasControl sender)
    {
        if (_legendFormat is null || sender.ActualWidth < 420 || sender.ActualHeight < 160)
        {
            return;
        }

        string[] labels = ["Hot path", "Rows (width)", "Estimate off \u226510\u00d7"];
        const float swatch = 18f;
        const float gap = 14f;
        const float padX = 10f;
        const float height = 26f;

        var widths = new float[labels.Length];
        for (var i = 0; i < labels.Length; i++)
        {
            using var layout = new CanvasTextLayout(ds, labels[i], _legendFormat, 200, height);
            widths[i] = (float)layout.LayoutBounds.Width;
        }

        var total = (padX * 2) + widths.Sum() + (labels.Length * (swatch + 5f)) + (gap * (labels.Length - 1));
        var x = 12f;
        var y = (float)sender.ActualHeight - 12f - height;

        ds.FillRoundedRectangle(x, y, total, height, NodeCorner, NodeCorner, _palette.Chrome);
        ds.DrawRoundedRectangle(x + 0.5f, y + 0.5f, total - 1, height - 1, NodeCorner, NodeCorner, _palette.Grid, 1f);

        var cx = x + padX;
        var midY = y + (height / 2f);
        for (var i = 0; i < labels.Length; i++)
        {
            switch (i)
            {
                case 0:
                    ds.DrawLine(cx, midY, cx + swatch, midY, _palette.Selection, 3f);
                    break;
                case 1:
                    ds.DrawLine(cx, midY, cx + swatch, midY, _palette.Edge, 2f);
                    break;
                default:
                    ds.DrawLine(cx, midY, cx + swatch, midY, _palette.Edge, 2f, DashedStroke);
                    break;
            }

            cx += swatch + 5f;
            ds.DrawText(labels[i], new Rect(cx, y, widths[i] + 4, height), _palette.TextSecondary, _legendFormat);
            cx += widths[i] + gap;
        }
    }

    /// <summary>
    /// The canvas surface: bg-0 with an 18px dotted grid in line, fixed to the screen like the
    /// mock-up's CSS background so it reads as a surface rather than as part of the plan.
    /// </summary>
    private void DrawGrid(CanvasDrawingSession ds, CanvasControl sender)
    {
        ds.Transform = Matrix3x2.Identity;
        ds.Clear(_palette.Surface);

        if (_gridBrush is null || _gridBrushColor != _palette.Grid)
        {
            _gridBrush?.Dispose();
            _gridTile?.Dispose();
            _gridTile = new CanvasRenderTarget(sender, 18, 18, sender.Dpi);
            using (var tile = _gridTile.CreateDrawingSession())
            {
                tile.Clear(Microsoft.UI.Colors.Transparent);
                tile.FillCircle(1f, 1f, 1f, _palette.Grid);
            }

            _gridBrush = new CanvasImageBrush(sender, _gridTile)
            {
                ExtendX = CanvasEdgeBehavior.Wrap,
                ExtendY = CanvasEdgeBehavior.Wrap,
            };
            _gridBrushColor = _palette.Grid;
        }

        ds.FillRectangle(0, 0, (float)sender.ActualWidth, (float)sender.ActualHeight, _gridBrush);
    }

    private CanvasRenderTarget? _gridTile;
    private CanvasImageBrush? _gridBrush;
    private Color _gridBrushColor;

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

        const float width = 132f;
        const float height = 92f;
        const float margin = 12f;
        const float padding = 8f;
        _minimapBounds = new Rect(sender.ActualWidth - width - margin, sender.ActualHeight - height - margin, width, height);

        ds.FillRoundedRectangle(_minimapBounds, NodeCorner, NodeCorner, _palette.Chrome);
        ds.DrawRoundedRectangle(
            new Rect(_minimapBounds.X + 0.5, _minimapBounds.Y + 0.5, _minimapBounds.Width - 1, _minimapBounds.Height - 1),
            NodeCorner,
            NodeCorner,
            _palette.ChromeBorder,
            1f);

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
            ds.DrawLine(Map(edge.Parent.CenterX, edge.Parent.Bottom), Map(edge.Child.CenterX, edge.Child.Y), _palette.NodeBorder, 0.7f);
        }

        foreach (var node in _layout.Nodes)
        {
            var position = Map(node.X, node.Y);
            var nodeWidth = Math.Max(3f, (float)node.Width * _minimapScale);
            var nodeHeight = Math.Max(2f, (float)node.Height * _minimapScale);
            ds.FillRoundedRectangle(position.X, position.Y, nodeWidth, nodeHeight, 1f, 1f, MinimapHeat(node.Node));
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

    /// <summary>Minimap nodes are line-ctl; only the selected node takes the accent.</summary>
    private Color MinimapHeat(PlanNode node) =>
        ReferenceEquals(node, SelectedNode) ? _palette.Selection : _palette.NodeBorder;

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
        return (float)(1.5 + (2.5 * Math.Clamp(t, 0, 1)));
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

    /// <summary>The colour a node's left edge and state word take.</summary>
    private enum NodeTone
    {
        None,
        Crit,
        Warn,
        Ok,
        Info,
    }

    /// <summary>
    /// The state a node card shows as a coloured left edge plus a word (never colour alone).
    /// A comparison on screen wins (better / worse); then Blame shows finding severity; the
    /// Estimate-skew metric keeps its diverging meaning as under / over; otherwise the
    /// Showplan's own warnings.
    /// </summary>
    private (string? Word, NodeTone Tone) NodeState(PlanNode node, Diagnostics.FindingSeverity? blameSeverity)
    {
        if (_diffDeltas.TryGetValue(node, out var delta))
        {
            return delta.Kind switch
            {
                PlanDiffKind.Added => ("new", NodeTone.None),
                _ when delta.CostDelta < -1e-9 => ("better", NodeTone.Ok),
                _ when delta.CostDelta > 1e-9 => ("worse", NodeTone.Crit),
                PlanDiffKind.Changed => ("changed", NodeTone.None),
                _ => (null, NodeTone.None),
            };
        }

        if (ColorMode == ColorMode.Blame)
        {
            return blameSeverity switch
            {
                Diagnostics.FindingSeverity.Critical => ("Critical", NodeTone.Crit),
                Diagnostics.FindingSeverity.Warning => ("Warning", NodeTone.Warn),
                Diagnostics.FindingSeverity.Info => ("Info", NodeTone.None),
                _ => (null, NodeTone.None),
            };
        }

        if (Metric == SizeMetric.EstimateSkew && node.EstimateErrorFactor is double factor && factor >= 2)
        {
            // Diverging: an underestimate (more rows than planned) starves memory grants and
            // picks the wrong joins; an overestimate wastes the grant. Different directions,
            // different words, different edges.
            return (node.ActualRows ?? 0) > node.EstimatedRowsTotal
                ? ($"{Format.Factor(factor)} under", NodeTone.Crit)
                : ($"{Format.Factor(factor)} over", NodeTone.Info);
        }

        return node.WorstWarning switch
        {
            WarningSeverity.Critical => ("Critical", NodeTone.Crit),
            WarningSeverity.Warning => ("Warning", NodeTone.Warn),
            _ => (null, NodeTone.None),
        };
    }

    private Color ToneColor(NodeTone tone, bool selected) => tone switch
    {
        NodeTone.Crit => selected ? _palette.SelCrit : _palette.Danger,
        NodeTone.Warn => selected ? _palette.SelWarn : _palette.Caution,
        NodeTone.Ok => selected ? _palette.SelFg : _palette.Ok,
        NodeTone.Info => selected ? _palette.SelFg : _palette.Selection,
        _ => selected ? _palette.SelFg : _palette.TextSecondary,
    };

    /// <summary>
    /// One node card, matching the mock-up: neutral bg-1 fill with a line-ctl border, a 3px
    /// coloured left edge for its state, the operator name with the state word top-right, a
    /// mono object line, the finding headline (Full detail), a rows line and a self-time line,
    /// and a 4px accent cost bar on bg-3 that carries the selected metric. Heat lives in the
    /// bar, not the fill, so the canvas never turns to confetti.
    /// </summary>
    private void DrawNode(CanvasDrawingSession ds, NodeLayout nl, bool dimmed, bool drawText)
    {
        var node = nl.Node;
        var w = (float)nl.Width;
        var h = (float)nl.Height;
        var hairline = 1f / _scale;
        var fraction = Math.Clamp(MetricFraction(node), 0, 1);
        var isSelected = ReferenceEquals(SelectedNode, node);
        var isHovered = !isSelected && ReferenceEquals(_hoveredNode, node);

        Diagnostics.FindingSeverity? blameSeverity = _blame.TryGetValue(node.NodeId, out var bs) ? bs : null;
        var (word, tone) = NodeState(node, blameSeverity);

        Color Dim(Color c) => dimmed ? _palette.Fade(c, 0.55) : c;

        // Card: fill, then the state edge, then the border on top.
        var fill = isSelected ? _palette.Sel : isHovered ? _palette.NodeHover : _palette.NodeBase;
        ds.FillGeometry(_nodeGeometry, Dim(fill));

        if (isSelected)
        {
            // Selected record: sel fill, a 2px inset leading edge and border in sel-edge.
            ds.FillRectangle(0, 0, 2f, h, _palette.SelEdge);
            ds.DrawGeometry(_nodeGeometry, _palette.SelEdge, hairline);
        }
        else
        {
            if (tone is not NodeTone.None)
            {
                ds.FillGeometry(_accentGeometry, Dim(ToneColor(tone, selected: false)));
            }

            ds.DrawGeometry(_nodeGeometry, Dim(_palette.NodeBorder), hairline);
        }

        if (!drawText)
        {
            // §8.7: below the LOD threshold text is unreadable, so the edge and bar carry it.
            DrawCostBar(ds, w, h - 12f, fraction, isSelected, dimmed);
            return;
        }

        var textLeft = TextLeftPad;
        var textWidth = w - TextLeftPad - TextRightPad;

        var primary = Dim(isSelected ? _palette.SelFg : _palette.TextPrimary);
        var body = Dim(isSelected ? _palette.SelFg : _palette.TextBody);
        var secondary = Dim(isSelected ? _palette.SelFg : _palette.TextSecondary);
        var code = Dim(isSelected ? _palette.SelFg : _palette.Code);

        // Title row: operator name, then the state word right-aligned on the same line.
        var wordWidth = 0f;
        if (word is not null)
        {
            using var wordLayout = new CanvasTextLayout(ds, word, _badgeFormat, textWidth, 18);
            wordWidth = (float)wordLayout.LayoutBounds.Width + 8f;
            ds.DrawText(word, new Rect(textLeft, TextTopPad + 1.5f, textWidth, 18), Dim(ToneColor(tone, isSelected)), _badgeFormat);
        }

        var parallelRoom = node.Parallel ? 12f : 0f;
        ds.DrawText(
            node.PhysicalOp,
            new Rect(textLeft, TextTopPad, Math.Max(10, textWidth - wordWidth - parallelRoom), 18),
            primary,
            _titleFormat);

        if (node.Parallel)
        {
            DrawParallelMarker(ds, textLeft + textWidth - wordWidth - 6f, TextTopPad + 4f, secondary);
        }

        var detail = EffectiveDetail();
        if (detail == Controls.LabelDetail.Minimal)
        {
            // Operator name and heat only.
            DrawCostBar(ds, w, h - 12f, fraction, isSelected, dimmed);
            if (nl.HasChildren)
            {
                DrawExpander(ds, nl, w, h, hairline, dimmed);
            }

            return;
        }

        var cursorY = TextTopPad + 18f;

        var subtitle = DescribeSubtitle(node);
        if (!string.IsNullOrEmpty(subtitle))
        {
            ds.DrawText(subtitle, new Rect(textLeft, cursorY, textWidth, 16), code, _subtitleFormat);
            cursorY += 16f;
        }

        if (detail == Controls.LabelDetail.Full && _verdicts.TryGetValue(node.NodeId, out var verdict))
        {
            cursorY += 3f;
            ds.DrawText(verdict.Title, new Rect(textLeft, cursorY, textWidth, 16), body, _metaFormat);
            cursorY += 16f;
        }

        cursorY += 2f;
        cursorY = DrawRowsLine(ds, node, textLeft, textWidth, cursorY, primary, secondary);
        DrawCostLine(ds, node, textLeft, textWidth, cursorY, secondary);

        DrawCostBar(ds, w, h - 12f, fraction, isSelected, dimmed);

        if (nl.HasChildren)
        {
            DrawExpander(ds, nl, w, h, hairline, dimmed);
        }
    }

    /// <summary>The 4px cost bar: accent on a bg-3 track (sel-fg on a 25% sel-fg track when selected).</summary>
    private void DrawCostBar(CanvasDrawingSession ds, float w, float y, double fraction, bool selected, bool dimmed)
    {
        var x = TextLeftPad;
        var width = w - TextLeftPad - TextRightPad;
        var track = selected ? _palette.WithAlpha(_palette.SelFg, 64) : _palette.Track;
        var bar = selected ? _palette.SelFg : _palette.Selection;
        if (dimmed)
        {
            track = _palette.Fade(track, 0.55);
            bar = _palette.Fade(bar, 0.55);
        }

        ds.FillRoundedRectangle(x, y, width, 4f, 2f, 2f, track);
        var filled = (float)Math.Max(width * 0.01, width * fraction);
        ds.FillRoundedRectangle(x, y, filled, 4f, 2f, 2f, bar);
    }

    /// <summary>"<b>50k</b> rows · 10k× off" on the left; the metric share, bold, on the right.</summary>
    private float DrawRowsLine(
        CanvasDrawingSession ds,
        PlanNode node,
        float textLeft,
        float textWidth,
        float y,
        Color strong,
        Color secondary)
    {
        string lead;
        string rest;
        if (node.ActualRows is double actual)
        {
            lead = Format.Rows(actual);
            rest = node.HasBadEstimate
                ? $" rows · {Format.Factor(node.EstimateErrorFactor!.Value)} off"
                : $" rows · est {Format.Rows(node.EstimatedRowsTotal)}";
        }
        else
        {
            lead = Format.Rows(node.EstimatedRowsTotal);
            rest = " rows estimated";
        }

        var share = _metricMax <= 0 ? 0 : MetricValue(node) / _metricMax;
        var shareText = Metric == SizeMetric.Efficiency ? $"{MetricValue(node):0.#}x" : Format.Percent(share);

        using (var right = new CanvasTextLayout(ds, shareText, _metaFormat, textWidth, 16))
        {
            right.HorizontalAlignment = CanvasHorizontalAlignment.Right;
            right.SetFontWeight(0, shareText.Length, Microsoft.UI.Text.FontWeights.SemiBold);
            ds.DrawTextLayout(right, textLeft, y, strong);
            var rightWidth = (float)right.LayoutBounds.Width + 8f;

            var text = lead + rest;
            using var left = new CanvasTextLayout(ds, text, _metaFormat, Math.Max(10, textWidth - rightWidth), 16);
            left.SetColor(0, text.Length, secondary);
            left.SetColor(0, lead.Length, strong);
            left.SetFontWeight(0, lead.Length, Microsoft.UI.Text.FontWeights.SemiBold);
            ds.DrawTextLayout(left, textLeft, y, secondary);
        }

        return y + 16f;
    }

    /// <summary>
    /// The second facts line: self time and its share of the query on an actual plan, or the
    /// metric's own reading where self time says nothing about it.
    /// </summary>
    private void DrawCostLine(
        CanvasDrawingSession ds,
        PlanNode node,
        float textLeft,
        float textWidth,
        float y,
        Color secondary)
    {
        var share = _metricMax <= 0 ? 0 : MetricValue(node) / _metricMax;
        string text;
        if (Metric == SizeMetric.Efficiency)
        {
            text = $"{MetricValue(node):0.#}x rows read vs. final output";
        }
        else if (Metric == SizeMetric.OperatorCost)
        {
            text = $"op cost {Format.Cost(node.EstimatedOperatorCost)} · {Format.Percent(share)}";
        }
        else if (node.HasRuntimeStats
                 && Statement?.Summary.QueryElapsedMs is double queryElapsedMs
                 && queryElapsedMs > 0)
        {
            var selfShare = Math.Clamp((node.SelfTimeMs ?? 0) / queryElapsedMs, 0, 1);
            text = $"self {Format.Milliseconds(node.SelfTimeMs ?? 0)} · {Format.Percent(selfShare)} of query";
        }
        else
        {
            text = $"subtree {Format.Cost(node.EstimatedSubtreeCost)}";
        }

        ds.DrawText(text, new Rect(textLeft, y, textWidth, 16), secondary, _metaFormat);
    }

    private void DrawParallelMarker(CanvasDrawingSession ds, float x, float y, Color color)
    {
        var hairline = Math.Max(1f, 1.4f / _scale);
        ds.DrawLine(x, y, x, y + 9f, color, hairline);
        ds.DrawLine(x + 4f, y, x + 4f, y + 9f, color, hairline);
    }

    /// <summary>Collapse handle: a 16px bg-3 circle with a line-ctl border ("+N" widens to a pill).</summary>
    private void DrawExpander(CanvasDrawingSession ds, NodeLayout nl, float w, float h, float hairline, bool dimmed)
    {
        var cx = w / 2f;
        var cy = h;

        var fill = dimmed ? _palette.Fade(_palette.Track, 0.5) : _palette.Track;
        var stroke = dimmed ? _palette.Fade(_palette.NodeBorder, 0.5) : _palette.NodeBorder;
        var text = dimmed ? _palette.Fade(_palette.TextBody, 0.5) : _palette.TextBody;

        var label = nl.IsCollapsed ? $"+{nl.HiddenDescendantCount}" : "\u2212";
        var halfWidth = nl.IsCollapsed ? Math.Max(HandleRadius, 4f + (label.Length * 3.6f)) : HandleRadius;

        ds.FillRoundedRectangle(cx - halfWidth, cy - HandleRadius, halfWidth * 2, HandleRadius * 2, HandleRadius, HandleRadius, fill);
        ds.DrawRoundedRectangle(cx - halfWidth, cy - HandleRadius, halfWidth * 2, HandleRadius * 2, HandleRadius, HandleRadius, stroke, hairline);
        ds.DrawText(
            label,
            new Rect(cx - halfWidth, cy - HandleRadius - 1, halfWidth * 2, HandleRadius * 2),
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
        Redraw();

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
        if (_hoveredNode is not null)
        {
            Redraw();
        }

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
            if ((dx * dx) + (dy * dy) <= 12 * 12)
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
