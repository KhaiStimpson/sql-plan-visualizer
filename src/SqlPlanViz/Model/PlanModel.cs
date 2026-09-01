namespace SqlPlanViz.Model;

/// <summary>
/// Normalized plan model (TDD §7). Deliberately plain CLR types with no dependency on
/// WinUI, Win2D or System.Xml — the parser, the layout engine and the view models all
/// consume the same tree.
/// </summary>
public enum WarningSeverity
{
    Info,
    Warning,
    Critical,
}

public sealed record PlanWarning(string Type, WarningSeverity Severity, string? Detail = null)
{
    /// <summary>Human-friendly label, e.g. "SpillToTempDb" → "Spill to tempdb".</summary>
    public string DisplayName => Type switch
    {
        "NoJoinPredicate" => "No join predicate",
        "SpillToTempDb" => "Spill to tempdb",
        "SortSpillDetails" => "Sort spilled to tempdb",
        "HashSpillDetails" => "Hash spilled to tempdb",
        "ExchangeSpillDetails" => "Exchange spilled to tempdb",
        "ColumnsWithNoStatistics" => "Columns with no statistics",
        "PlanAffectingConvert" => "Plan-affecting conversion",
        "MemoryGrantWarning" => "Memory grant issue",
        "UnmatchedIndexes" => "Unmatched (filtered) index",
        "SpatialGuess" => "Spatial cardinality guess",
        "FullUpdateForOnlineIndexBuild" => "Full update for online index build",
        "Wait" => "Significant wait",
        _ => SplitCamelCase(Type),
    };

    private static string SplitCamelCase(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return s;
        }

        var sb = new System.Text.StringBuilder(s.Length + 8);
        for (var i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1]))
            {
                sb.Append(' ');
                sb.Append(char.ToLowerInvariant(s[i]));
            }
            else
            {
                sb.Append(s[i]);
            }
        }

        return sb.ToString();
    }
}

public sealed class PlanNode
{
    public int NodeId { get; init; }

    public string PhysicalOp { get; init; } = string.Empty;

    public string LogicalOp { get; init; } = string.Empty;

    /// <summary>Rows Showplan estimated <em>per execution</em>.</summary>
    public double EstimatedRows { get; init; }

    /// <summary>
    /// Rebinds + rewinds + 1. Showplan reports EstimateRows per execution but ActualRows
    /// as a total, so comparing them directly on the inner side of a nested loop reports a
    /// skew that isn't there. <see cref="EstimatedRowsTotal"/> is the comparable number.
    /// </summary>
    public double EstimatedExecutions { get; init; } = 1;

    /// <summary>Actual rows, summed across threads. Null on an estimated-only plan.</summary>
    public double? ActualRows { get; init; }

    public double EstimatedSubtreeCost { get; init; }

    /// <summary>Subtree cost minus the children's subtree costs — this operator alone.</summary>
    public double EstimatedOperatorCost { get; init; }

    public double EstimatedCpuCost { get; init; }

    public double EstimatedIoCost { get; init; }

    /// <summary>Max across threads, when present.</summary>
    public double? ActualElapsedMs { get; init; }

    public double? ActualCpuMs { get; init; }

    public int? ActualExecutions { get; init; }

    public bool Parallel { get; init; }

    /// <summary>Table/index touched, if any.</summary>
    public string? ObjectName { get; init; }

    /// <summary>Just the table, unbracketed — <see cref="ObjectName"/> also carries schema and index.</summary>
    public string? ObjectTable { get; init; }

    /// <summary>The alias the query gave this table, if it gave one. This is what the SQL text says.</summary>
    public string? ObjectAlias { get; init; }

    public string? Predicate { get; init; }

    public string? SeekPredicate { get; init; }

    public IReadOnlyList<string> OutputList { get; init; } = [];

    public IReadOnlyList<PlanWarning> Warnings { get; init; } = [];

    public IReadOnlyList<PlanNode> Children { get; init; } = [];

    /// <summary>Set on the grant-owning operator only (typically the root). Null elsewhere.</summary>
    public MemoryGrantInfo? MemoryGrant { get; init; }

    /// <summary>Per-thread runtime counters for this operator. Empty on estimated-only plans.</summary>
    public IReadOnlyList<ThreadRuntime> PerThread { get; init; } = [];

    /// <summary>
    /// True when this operator ran on more than one thread and the busiest thread did more
    /// than double the mean row count — a sign of parallel skew invisible in the aggregate.
    /// </summary>
    public bool HasThreadSkew
    {
        get
        {
            if (PerThread.Count <= 1)
            {
                return false;
            }

            var mean = PerThread.Average(t => t.ActualRows);
            if (mean <= 0)
            {
                return false;
            }

            var max = PerThread.Max(t => t.ActualRows);
            return max / mean > 2;
        }
    }

    public double EstimatedRowsTotal => EstimatedRows * EstimatedExecutions;

    public bool HasRuntimeStats => ActualRows.HasValue;

    /// <summary>
    /// How far the estimate missed, as a factor ≥ 1 in whichever direction it erred.
    /// Null on estimated-only plans.
    /// </summary>
    public double? EstimateErrorFactor
    {
        get
        {
            if (ActualRows is not double actual)
            {
                return null;
            }

            // Clamp to 1 row: a 0-vs-3 row miss is noise, not a 3x problem, and dividing
            // by zero rows would make every empty operator look catastrophic.
            var a = Math.Max(actual, 1);
            var e = Math.Max(EstimatedRowsTotal, 1);
            return Math.Max(a, e) / Math.Min(a, e);
        }
    }

    /// <summary>The classic bad-estimate signal (TDD §8): off by more than ~10x.</summary>
    public bool HasBadEstimate => EstimateErrorFactor >= 10;

    /// <summary>
    /// This operator's own elapsed time — <see cref="ActualElapsedMs"/> minus the slowest
    /// child's elapsed time. Elapsed time is wall-clock and inclusive of children, so
    /// subtree cost alone almost always points at the wrong node for "what is slow"; this is
    /// the real answer. Null on estimated-only plans.
    /// </summary>
    public double? SelfTimeMs
    {
        get
        {
            if (ActualElapsedMs is not double elapsed)
            {
                return null;
            }

            var maxChildElapsed = Children.Count == 0 ? 0 : Children.Max(c => c.ActualElapsedMs ?? 0);
            return Math.Max(0, elapsed - maxChildElapsed);
        }
    }

    /// <summary>
    /// This operator's own CPU time — <see cref="ActualCpuMs"/> minus the slowest child's CPU
    /// time. Like <see cref="SelfTimeMs"/>, Showplan's per-operator CPU counters are cumulative
    /// from the leaves up, not exclusive, so the same subtraction applies. Unlike elapsed time,
    /// CPU-milliseconds are conserved regardless of parallelism — they're the additive basis
    /// <c>Diagnostics/TimeAttribution</c> uses beneath a parallel operator (hot-path-plan.md
    /// Phase 2). Null on estimated-only plans.
    /// </summary>
    public double? CpuSelfMs
    {
        get
        {
            if (ActualCpuMs is not double cpu)
            {
                return null;
            }

            var maxChildCpu = Children.Count == 0 ? 0 : Children.Max(c => c.ActualCpuMs ?? 0);
            return Math.Max(0, cpu - maxChildCpu);
        }
    }

    public WarningSeverity? WorstWarning =>
        Warnings.Count == 0 ? null : Warnings.Max(w => w.Severity);

    public string DisplayName =>
        string.IsNullOrEmpty(LogicalOp) || LogicalOp == PhysicalOp
            ? PhysicalOp
            : PhysicalOp;

    /// <summary>Depth-first walk including this node.</summary>
    public IEnumerable<PlanNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var n in child.DescendantsAndSelf())
            {
                yield return n;
            }
        }
    }
}

public sealed class MissingIndexSuggestion
{
    public string Database { get; init; } = string.Empty;

    public string Schema { get; init; } = string.Empty;

    public string Table { get; init; } = string.Empty;

    public IReadOnlyList<string> EqualityColumns { get; init; } = [];

    public IReadOnlyList<string> InequalityColumns { get; init; } = [];

    public IReadOnlyList<string> IncludedColumns { get; init; } = [];

    public double ImpactPercent { get; init; }

    /// <summary>Generated by the app — Showplan carries the columns, not the DDL.</summary>
    public string SuggestedCreateStatement { get; init; } = string.Empty;

    public string DisplayTarget => $"{Trim(Schema)}.{Trim(Table)}";

    public string ImpactText => ImpactPercent.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + "%";

    public string ColumnSummary
    {
        get
        {
            var parts = new List<string>();
            if (EqualityColumns.Count > 0)
            {
                parts.Add($"= {string.Join(", ", EqualityColumns)}");
            }

            if (InequalityColumns.Count > 0)
            {
                parts.Add($"> {string.Join(", ", InequalityColumns)}");
            }

            if (IncludedColumns.Count > 0)
            {
                parts.Add($"include {string.Join(", ", IncludedColumns)}");
            }

            return string.Join("  ·  ", parts);
        }
    }

    private static string Trim(string s) => s.Trim('[', ']');
}

public sealed class PlanSummary
{
    public string StatementText { get; init; } = string.Empty;

    public double TotalSubtreeCost { get; init; }

    public int DegreeOfParallelism { get; init; }

    /// <summary>From QueryTimeStats — actual plans only.</summary>
    public double? QueryElapsedMs { get; init; }

    public double? QueryCpuMs { get; init; }

    public MemoryGrantInfo? MemoryGrant { get; init; }

    public IReadOnlyList<WaitStat> Waits { get; init; } = [];

    public IReadOnlyList<ParameterInfo> Parameters { get; init; } = [];

    public IReadOnlyList<StatisticsUsage> StatisticsUsed { get; init; } = [];

    public CompileInfo? Compile { get; init; }

    /// <summary>First line of the statement, for pickers and tab headers.</summary>
    public string ShortText
    {
        get
        {
            var text = StatementText.Replace('\r', ' ').Replace('\n', ' ').Trim();
            while (text.Contains("  "))
            {
                text = text.Replace("  ", " ");
            }

            return text.Length <= 90 ? text : text[..90] + "…";
        }
    }
}

/// <summary>One statement's plan. A .sqlplan for a batch carries several of these.</summary>
public sealed class PlanStatement
{
    public PlanSummary Summary { get; init; } = new();

    public PlanNode Root { get; init; } = new();

    public IReadOnlyList<MissingIndexSuggestion> MissingIndexes { get; init; } = [];

    private IReadOnlyList<PlanNode>? _allNodes;

    public IReadOnlyList<PlanNode> AllNodes => _allNodes ??= Root.DescendantsAndSelf().ToList();

    private IReadOnlyList<Diagnostics.PlanFinding>? _findings;

    /// <summary>Lazily-computed diagnostics for this statement, so the UI can bind without orchestrating the engine.</summary>
    public IReadOnlyList<Diagnostics.PlanFinding> Findings => _findings ??= new Diagnostics.RuleEngine().Analyse(this);

    private string? _fingerprint;

    public string Fingerprint => _fingerprint ??= Diagnostics.PlanFingerprint.Compute(this);

    public bool HasRuntimeStats => AllNodes.Any(n => n.HasRuntimeStats);

    public IEnumerable<(PlanNode Node, PlanWarning Warning)> AllWarnings =>
        AllNodes.SelectMany(n => n.Warnings.Select(w => (n, w)));

    public double MaxSubtreeCost => AllNodes.Max(n => n.EstimatedSubtreeCost);

    public double MaxOperatorCost => AllNodes.Max(n => n.EstimatedOperatorCost);

    public double MaxActualRows => AllNodes.Max(n => n.ActualRows ?? 0);

    public double MaxEstimatedRows => AllNodes.Max(n => n.EstimatedRowsTotal);

    public double MaxElapsedMs => AllNodes.Max(n => n.ActualElapsedMs ?? 0);

    public double MaxSelfTimeMs => AllNodes.Max(n => n.SelfTimeMs ?? 0);
}

public sealed class ExecutionPlan
{
    public IReadOnlyList<PlanStatement> Statements { get; init; } = [];

    /// <summary>Where this plan came from, for the title bar.</summary>
    public string? SourceName { get; init; }

    public bool HasRuntimeStats => Statements.Any(s => s.HasRuntimeStats);
}
