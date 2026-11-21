using SqlPlanViz.Diagnostics;
using SqlPlanViz.Themes;
using Windows.UI;

namespace SqlPlanViz.Controls;

/// <summary>Whether node colour is driven by a <see cref="SizeMetric"/> or by findings (tuning-roadmap.md Phase 4.5).</summary>
public enum ColorMode
{
    Metric,
    Blame,
}

/// <summary>Which metric drives node heat and edge thickness (TDD §8).</summary>
public enum SizeMetric
{
    SubtreeCost,
    OperatorCost,
    ActualRows,
    ElapsedTime,

    /// <summary>Diverging: how far and in which direction the row estimate missed (tuning-roadmap.md Phase 4.1).</summary>
    EstimateSkew,

    /// <summary>This node's rows ÷ rows the query finally returned (tuning-roadmap.md Phase 4.2) — finds "read 2M rows to give you 40" instantly.</summary>
    Efficiency,

    /// <summary><see cref="PlanNode.SelfTimeMs"/> (tuning-roadmap.md Phase 4.3) — the real "what is slow", often a different node than subtree cost suggests.</summary>
    SelfTime,
}

/// <summary>
/// How much text a node shows, independent of zoom (hot-path-plan.md Phase 1). The effective
/// density is the more restrictive of this and the zoom-driven level-of-detail threshold.
/// </summary>
public enum LabelDetail
{
    /// <summary>Operator name and heat only.</summary>
    Minimal,

    /// <summary>Adds subtitle, rows, and cost/self-time lines.</summary>
    Standard,

    /// <summary>Adds the verdict line (the node's highest-severity finding).</summary>
    Full,
}

/// <summary>
/// Theme-aware colours for the plan canvas and flame view. Win2D draws outside the XAML
/// resource system, so every value comes from <see cref="GaugeTokens"/> (generated from the
/// same tokens.json as Themes/GaugeTheme.xaml) and is rebuilt whenever the app theme changes.
/// Never hand-copy hex here.
/// </summary>
public sealed class PlanPalette
{
    public required bool IsDark { get; init; }

    /// <summary>The raw Gauge roles, for anything the named properties below do not cover.</summary>
    public required GaugeTokenSet Tokens { get; init; }

    /// <summary>Canvas surface: bg-0.</summary>
    public required Color Surface { get; init; }

    /// <summary>Dotted background grid: line.</summary>
    public required Color Grid { get; init; }

    /// <summary>Node card fill: bg-1.</summary>
    public required Color NodeBase { get; init; }

    /// <summary>Node card fill on hover: bg-2.</summary>
    public required Color NodeHover { get; init; }

    /// <summary>Node card border and neutral edges: line-ctl.</summary>
    public required Color NodeBorder { get; init; }

    /// <summary>Operator name and emphasised facts: fg-0.</summary>
    public required Color TextPrimary { get; init; }

    /// <summary>Body text (the finding headline): fg-1.</summary>
    public required Color TextBody { get; init; }

    /// <summary>Facts and labels: fg-2.</summary>
    public required Color TextSecondary { get; init; }

    /// <summary>Quiet metadata: fg-3.</summary>
    public required Color TextTertiary { get; init; }

    /// <summary>The mono object line: code.</summary>
    public required Color Code { get; init; }

    public required Color Edge { get; init; }

    public required Color EdgeEstimated { get; init; }

    /// <summary>The one accent: hot path, cost bar, focus, minimap viewport.</summary>
    public required Color Selection { get; init; }

    public required Color Danger { get; init; }

    public required Color Caution { get; init; }

    public required Color Ok { get; init; }

    /// <summary>Selected record: sel fill, sel-fg text, sel-crit / sel-warn state words, sel-edge.</summary>
    public required Color Sel { get; init; }

    public required Color SelFg { get; init; }

    public required Color SelCrit { get; init; }

    public required Color SelWarn { get; init; }

    public required Color SelEdge { get; init; }

    /// <summary>Raised chrome on the canvas (minimap, legend): bg-2, with line-2 / line borders.</summary>
    public required Color Chrome { get; init; }

    public required Color ChromeBorder { get; init; }

    /// <summary>Cost bar track and collapse handle: bg-3.</summary>
    public required Color Track { get; init; }

    /// <summary>Heat ramp stops: cool → warm → hot (minimap and legacy callers).</summary>
    public required Color HeatCool { get; init; }

    public required Color HeatWarm { get; init; }

    public required Color HeatHot { get; init; }

    /// <summary>The sequential tm-1 (hottest) … tm-9 ramp for the flame view.</summary>
    public required IReadOnlyList<Color> Ramp { get; init; }

    public static PlanPalette For(bool isDark)
    {
        var t = isDark ? GaugeTokens.Dark : GaugeTokens.Light;
        return new PlanPalette
        {
            IsDark = isDark,
            Tokens = t,
            Surface = t.Bg0,
            Grid = t.Line,
            NodeBase = t.Bg1,
            NodeHover = t.Bg2,
            NodeBorder = t.LineCtl,
            TextPrimary = t.Fg0,
            TextBody = t.Fg1,
            TextSecondary = t.Fg2,
            TextTertiary = t.Fg3,
            Code = t.Code,
            Edge = t.LineCtl,
            EdgeEstimated = t.LineCtl,
            Selection = t.Accent,
            Danger = t.Crit,
            Caution = t.Warn,
            Ok = t.Ok,
            Sel = t.Sel,
            SelFg = t.SelFg,
            SelCrit = t.SelCrit,
            SelWarn = t.SelWarn,
            SelEdge = t.SelEdge,
            Chrome = t.Bg2,
            ChromeBorder = t.Line2,
            Track = t.Bg3,
            HeatCool = t.LineCtl,
            HeatWarm = t.Warn,
            HeatHot = t.Crit,
            Ramp = [t.Tm1, t.Tm2, t.Tm3, t.Tm4, t.Tm5, t.Tm6, t.Tm7, t.Tm8, t.Tm9],
        };
    }

    /// <summary>
    /// A Windows contrast theme replaces Gauge's palette (PLATFORMS.md): every role maps to a
    /// system colour, so selection, focus and the hot path stay visible.
    /// </summary>
    public static PlanPalette HighContrast()
    {
        var ui = new Windows.UI.ViewManagement.UISettings();
        Color C(Windows.UI.ViewManagement.UIElementType type) => ui.UIElementColor(type);
        var window = C(Windows.UI.ViewManagement.UIElementType.Window);
        var text = C(Windows.UI.ViewManagement.UIElementType.WindowText);
        var highlight = C(Windows.UI.ViewManagement.UIElementType.Highlight);
        var highlightText = C(Windows.UI.ViewManagement.UIElementType.HighlightText);
        var gray = C(Windows.UI.ViewManagement.UIElementType.GrayText);
        var link = C(Windows.UI.ViewManagement.UIElementType.Hotlight);
        var tokens = GaugeTokens.Dark;
        return new PlanPalette
        {
            IsDark = true,
            Tokens = tokens,
            Surface = window,
            Grid = window,
            NodeBase = window,
            NodeHover = window,
            NodeBorder = text,
            TextPrimary = text,
            TextBody = text,
            TextSecondary = text,
            TextTertiary = gray,
            Code = text,
            Edge = text,
            EdgeEstimated = text,
            Selection = highlight,
            Danger = text,
            Caution = text,
            Ok = text,
            Sel = highlight,
            SelFg = highlightText,
            SelCrit = highlightText,
            SelWarn = highlightText,
            SelEdge = highlightText,
            Chrome = window,
            ChromeBorder = text,
            Track = gray,
            HeatCool = text,
            HeatWarm = text,
            HeatHot = link,
            Ramp = [window, window, window, window, window, window, window, window, window],
        };
    }

    /// <summary>Whether a Windows contrast theme is on.</summary>
    public static bool IsHighContrast => new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;

    /// <summary>The palette for the current state: high contrast wins over the app theme.</summary>
    public static PlanPalette Current(bool isDark) => IsHighContrast ? HighContrast() : For(isDark);

    /// <summary>The tm-1 … tm-9 step for a 0..1 share: the largest share gets tm-1.</summary>
    public Color RampStep(double fraction)
    {
        var t = Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0, 1);
        var index = (int)Math.Round((1 - Math.Pow(t, 0.55)) * (Ramp.Count - 1));
        return Ramp[Math.Clamp(index, 0, Ramp.Count - 1)];
    }

    /// <summary>Saturated heat colour for the left accent strip and edge emphasis.</summary>
    public Color Heat(double fraction)
    {
        var t = Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0, 1);

        // Perceptually the interesting range is the top third of the scale, so bias the
        // ramp: most operators in a plan are cheap and should stay visually quiet.
        t = Math.Pow(t, 0.55);

        return t < 0.5
            ? Lerp(HeatCool, HeatWarm, t * 2)
            : Lerp(HeatWarm, HeatHot, (t - 0.5) * 2);
    }

    /// <summary>
    /// Node fill: mostly the neutral card colour with a wash of heat. Fully saturated
    /// boxes turn a large plan into confetti; the accent strip carries the signal.
    /// </summary>
    public Color NodeFill(double fraction, bool dimmed)
    {
        var t = Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0, 1);
        var fill = Lerp(NodeBase, Heat(t), 0.06 + (0.22 * Math.Pow(t, 0.7)));
        return dimmed ? Lerp(Surface, fill, 0.35) : fill;
    }

    /// <summary>
    /// Diverging heat centred at 0 (tuning-roadmap.md Phase 4.1): negative is an
    /// overestimate (blue — wastes memory grants), positive is an underestimate (red —
    /// causes spills and bad join choices), and values near 0 fade to neutral grey. Unlike
    /// <see cref="Heat"/>, direction matters here, so one unidirectional ramp cannot say both.
    /// </summary>
    public Color Diverging(double signedFraction)
    {
        var t = Math.Clamp(double.IsNaN(signedFraction) ? 0 : signedFraction, -1, 1);
        var magnitude = Math.Pow(Math.Abs(t), 0.6);

        var neutral = Lerp(NodeBorder, TextTertiary, 0.4);
        return t >= 0
            ? Lerp(neutral, Danger, magnitude)
            : Lerp(neutral, HeatCool, magnitude);
    }

    /// <summary>Colour for a node implicated by a finding, in <see cref="ColorMode.Blame"/> (Phase 4.5).</summary>
    public Color FindingAccent(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => Danger,
        FindingSeverity.Warning => Caution,
        _ => HeatCool,
    };

    public Color Fade(Color color, double amount) => Lerp(Surface, color, 1 - Math.Clamp(amount, 0, 1));

    public Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            255,
            (byte)Math.Round(a.R + ((b.R - a.R) * t)),
            (byte)Math.Round(a.G + ((b.G - a.G) * t)),
            (byte)Math.Round(a.B + ((b.B - a.B) * t)));
    }

}
