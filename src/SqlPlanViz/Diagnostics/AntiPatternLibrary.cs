namespace SqlPlanViz.Diagnostics;

public sealed record AntiPatternInfo(string RuleId, string Name, string Summary, string Explanation);

/// <summary>Named, searchable explanations for every built-in diagnostic rule.</summary>
public static class AntiPatternLibrary
{
    public static IReadOnlyList<AntiPatternInfo> All { get; } =
    [
        new("estimate-blowup-origin", "Cardinality Cascade", "The first estimate error poisons every choice above it.", "Fix the deepest origin first. Join choices, grants, and parallelism above it are usually collateral damage rather than separate root causes."),
        new("key-lookup-storm", "Lookup Storm", "A small random read is repeated once per outer row.", "Lookups are healthy for a handful of rows and punishing at scale. A covering index can remove them, but its write and storage cost must be justified."),
        new("residual-predicate-scan", "Rows Read Tax", "The access method reads far more rows than it returns.", "A residual predicate is evaluated after access. Aligning index keys with the predicate can reduce reads instead of merely filtering them later."),
        new("implicit-conversion", "Conversion Barrier", "A type conversion prevents direct index navigation.", "Match parameter and column data types. Changing a column is a schema migration; changing the caller's parameter is usually safer."),
        new("spill-to-tempdb", "Tempdb Spill", "A memory-consuming operator exceeded its grant.", "Spills are often downstream evidence of a bad estimate. Repair statistics or query shape before reaching for a memory-grant hint."),
        new("non-sargable-predicate", "SARG Barrier", "An expression hides an indexed column from the access method.", "Rewrite the predicate so the bare column is compared to a value or range. Leading-wildcard searches need a different search strategy."),
        new("parameter-sniffing", "Sniffing Skew", "The compiled value and runtime value demand different plans.", "Choose mitigation by workload: recompile spends CPU, OPTIMIZE FOR fixes a representative value, and generic plans trade peak speed for stability."),
        new("parallelism-skew", "Hot Worker", "One parallel worker carries much more data than its peers.", "Skew leaves most workers idle while one finishes the job. Investigate uneven join keys, repartitioning, and upstream estimates."),
        new("stale-statistics", "Stale Map", "The optimizer's data distribution map no longer matches the table.", "Refresh the exact statistic and confirm sampling is adequate. Automatic updates may be too late for large or rapidly changing tables."),
        new("optimizer-gave-up", "Search Cut Short", "Optimization stopped before the search space was fully explored.", "Timeout and memory-limit aborts mean the chosen plan is the best found so far, not necessarily the best available. Simplify the query shape first."),
        new("fat-inner-side-loop", "Heavy Inner Loop", "An expensive inner subtree is repeated for every outer row.", "Make the inner access cheap with a selective index or reshape the join. A hash-join hint is a diagnostic fallback, not a default fix."),
        new("spool-trap", "The Spool Trap", "A temporary worktable is repeatedly rebound.", "Spools can protect correctness or avoid repeated work, but high rebind counts often reveal correlated subqueries or ORM-generated repetition."),
        new("scalar-udf", "Function Fog", "Function work is hidden or estimated with a fixed guess.", "Inlining exposes relational work to the optimizer. Verify SQL Server's automatic UDF inlining eligibility before rewriting by hand."),
        new("wait-dominated", "Wrong Battlefield", "Most elapsed time is outside plan execution work.", "Lock, I/O, or external waits need concurrency or infrastructure investigation. Rewriting operators will not remove time spent waiting elsewhere."),
        new("wide-update", "Index Write Fan-out", "One data change maintains many secondary indexes.", "Every extra index taxes inserts, updates, deletes, logging, and locking. Consolidate only after checking read dependencies."),
        new("missing-index-merge", "Index Suggestion Pile-up", "Overlapping missing-index hints describe one broader need.", "Merge compatible suggestions, compare them with existing indexes, and judge table size and write rate before creating anything."),
    ];

    private static readonly IReadOnlyDictionary<string, AntiPatternInfo> ByRule =
        All.ToDictionary(item => item.RuleId, StringComparer.OrdinalIgnoreCase);

    public static AntiPatternInfo For(string ruleId) => ByRule.GetValueOrDefault(ruleId)
        ?? new AntiPatternInfo(ruleId, ruleId, "A detected plan pattern.", "Review the measured evidence and proposed fixes for this finding.");
}
