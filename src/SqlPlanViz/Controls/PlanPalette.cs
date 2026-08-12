using SqlPlanViz.Diagnostics;
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
/// Theme-aware colours for the plan canvas. Win2D draws outside the XAML resource system,
/// so the Fluent palette is mirrored here and rebuilt whenever the app theme changes.
/// </summary>
public sealed class PlanPalette
{
    public required bool IsDark { get; init; }

    public required Color Surface { get; init; }

    public required Color NodeBase { get; init; }

    public required Color NodeBorder { get; init; }

    public required Color TextPrimary { get; init; }

    public required Color TextSecondary { get; init; }

    public required Color TextTertiary { get; init; }

    public required Color Edge { get; init; }

    public required Color EdgeEstimated { get; init; }

    public required Color Selection { get; init; }

    public required Color Danger { get; init; }

    public required Color Caution { get; init; }

    /// <summary>Heat ramp stops: cool → warm → hot.</summary>
    public required Color HeatCool { get; init; }

    public required Color HeatWarm { get; init; }

    public required Color HeatHot { get; init; }

    public static PlanPalette For(bool isDark) => isDark
        ? new PlanPalette
        {
            IsDark = true,
            Surface = Rgb(0x1A, 0x1A, 0x1E),
            NodeBase = Rgb(0x2A, 0x2D, 0x34),
            NodeBorder = Rgb(0x3E, 0x43, 0x4C),
            TextPrimary = Rgb(0xF2, 0xF3, 0xF5),
            TextSecondary = Rgb(0xA8, 0xAE, 0xB8),
            TextTertiary = Rgb(0x7C, 0x83, 0x8E),
            Edge = Rgb(0x6E, 0x78, 0x86),
            EdgeEstimated = Rgb(0x4C, 0x54, 0x60),
            Selection = Rgb(0x60, 0xB0, 0xFF),
            Danger = Rgb(0xFF, 0x6B, 0x63),
            Caution = Rgb(0xFF, 0xC1, 0x4E),
            HeatCool = Rgb(0x4A, 0x9E, 0xFF),
            HeatWarm = Rgb(0xF2, 0xB0, 0x3C),
            HeatHot = Rgb(0xFF, 0x5A, 0x52),
        }
        : new PlanPalette
        {
            IsDark = false,
            Surface = Rgb(0xF7, 0xF8, 0xFA),
            NodeBase = Rgb(0xFF, 0xFF, 0xFF),
            NodeBorder = Rgb(0xD8, 0xDD, 0xE4),
            TextPrimary = Rgb(0x17, 0x1A, 0x1F),
            TextSecondary = Rgb(0x5A, 0x63, 0x70),
            TextTertiary = Rgb(0x8A, 0x93, 0xA0),
            Edge = Rgb(0x9A, 0xA4, 0xB2),
            EdgeEstimated = Rgb(0xC4, 0xCC, 0xD6),
            Selection = Rgb(0x00, 0x67, 0xC0),
            Danger = Rgb(0xC4, 0x2B, 0x1C),
            Caution = Rgb(0xB8, 0x7A, 0x00),
            HeatCool = Rgb(0x3C, 0x84, 0xD8),
            HeatWarm = Rgb(0xE8, 0x9A, 0x1C),
            HeatHot = Rgb(0xD9, 0x3A, 0x2F),
        };

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

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
}
