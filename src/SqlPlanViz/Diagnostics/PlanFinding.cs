using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics;

public enum FindingSeverity
{
    Info,
    Warning,
    Critical,
}

public enum FindingConfidence
{
    Possible,
    Likely,
    High,
}

public enum FixKind
{
    Index,
    Rewrite,
    Statistics,
    Hint,
    Configuration,
    Investigate,
}

/// <summary>
/// A suggested remedy for a <see cref="PlanFinding"/>. <see cref="Caveat"/> is required
/// whenever the fix mutates the database (tuning-roadmap.md ground rules) — index
/// suggestions must always state their write cost.
/// </summary>
public sealed record Fix(string Summary, string? Snippet, string? Caveat, FixKind Kind);

/// <summary>
/// One diagnosed problem in a plan, produced by an <see cref="IPlanRule"/>. Text is always
/// templated with the real numbers from <see cref="Nodes"/> — never a generic blurb.
/// </summary>
public sealed record PlanFinding
{
    /// <summary>Stable slug, e.g. "key-lookup-storm".</summary>
    public required string RuleId { get; init; }

    /// <summary>Templated with real numbers from the offending node(s).</summary>
    public required string Title { get; init; }

    public required FindingSeverity Severity { get; init; }

    public required FindingConfidence Confidence { get; init; }

    /// <summary>The node(s) this finding is about; may span several.</summary>
    public required IReadOnlyList<PlanNode> Nodes { get; init; }

    /// <summary>The explanation, phrased around this finding's actual numbers.</summary>
    public required string Why { get; init; }

    public IReadOnlyList<Fix> Fixes { get; init; } = [];

    /// <summary>0..1 fraction of total plan cost/time this finding accounts for.</summary>
    public double ImpactFraction { get; init; }
}
