namespace SqlPlanViz.Editing;

/// <summary>What a gutter mark says about the line it sits on (live-plan-editor-plan.md Phase 5).</summary>
public enum GutterMarkKind
{
    Improved,
    Regressed,
    Added,
    Error,
}

/// <summary>
/// A mark in the editor's gutter column. <see cref="NodeId"/> is the operator the mark blames,
/// so clicking the mark can select that node on the plan canvas.
/// </summary>
public sealed class GutterMark
{
    public int Line { get; init; }

    public GutterMarkKind Kind { get; init; }

    public string Tooltip { get; init; } = string.Empty;

    /// <summary>Plan node this mark points at, or null when the mark is not operator-derived.</summary>
    public int? NodeId { get; init; }
}

/// <summary>End-of-line annotation text — the noisiest of the Phase 5 surfaces, hence toggleable.</summary>
public sealed class InlineAnnotation
{
    public int Line { get; init; }

    public string Text { get; init; } = string.Empty;

    public GutterMarkKind Kind { get; init; } = GutterMarkKind.Added;
}

/// <summary>A red squiggle under a compile error, positioned in document offsets (Phase 4).</summary>
public sealed class EditorSquiggle
{
    public int Start { get; init; }

    public int Length { get; init; } = 1;

    public string Message { get; init; } = string.Empty;
}
