namespace SqlPlanViz.Model;

/// <summary>
/// Diagnostics-oriented Showplan data (tuning-roadmap.md Phase 1): memory grants, waits,
/// per-thread runtime, parameters, statistics usage and compile info. Kept alongside
/// <see cref="PlanNode"/>/<see cref="PlanSummary"/> in <c>PlanModel.cs</c>'s spirit — plain
/// CLR types, no WinUI/Win2D/System.Xml dependency. All fields nullable: estimated-only
/// plans and older SQL Server versions omit most of them, and a missing element must never
/// throw.
/// </summary>
public sealed class MemoryGrantInfo
{
    public double SerialRequiredMemoryKb { get; init; }

    public double SerialDesiredMemoryKb { get; init; }

    public double? RequestedMemoryKb { get; init; }

    public double? GrantedMemoryKb { get; init; }

    public double? MaxUsedMemoryKb { get; init; }

    public double? GrantWaitTimeMs { get; init; }

    /// <summary>Fraction of the granted memory actually used. Null when either figure is missing.</summary>
    public double? UsedFraction =>
        MaxUsedMemoryKb is double used && GrantedMemoryKb is double granted && granted > 0
            ? used / granted
            : null;
}

public sealed record WaitStat(string WaitType, double WaitTimeMs, long WaitCount);

/// <summary>One thread's runtime counters for a single operator.</summary>
public sealed record ThreadRuntime(int Thread, double ActualRows, double? ElapsedMs, double? CpuMs);

public sealed class ParameterInfo
{
    public string Name { get; init; } = string.Empty;

    public string? DataType { get; init; }

    /// <summary>Value the plan was optimised for.</summary>
    public string? CompiledValue { get; init; }

    /// <summary>Value it actually ran with.</summary>
    public string? RuntimeValue { get; init; }

    public bool Sniffed => CompiledValue != RuntimeValue;
}

public sealed class StatisticsUsage
{
    public string Database { get; init; } = string.Empty;

    public string Schema { get; init; } = string.Empty;

    public string Table { get; init; } = string.Empty;

    public string StatisticsName { get; init; } = string.Empty;

    public double? SamplingPercent { get; init; }

    public DateTime? LastUpdate { get; init; }

    public long? ModificationCount { get; init; }
}

public sealed class CompileInfo
{
    public double? CompileTimeMs { get; init; }

    public double? CompileCpuMs { get; init; }

    public double? CompileMemoryKb { get; init; }

    /// <summary>"TimeOut" | "MemoryLimitExceeded" | "GoodEnoughPlanFound", when present.</summary>
    public string? EarlyAbortReason { get; init; }

    public string? CardinalityEstimationModelVersion { get; init; }

    public IReadOnlyList<string> TraceFlags { get; init; } = [];

    public IReadOnlyDictionary<string, string> SetOptions { get; init; } = new Dictionary<string, string>();
}
