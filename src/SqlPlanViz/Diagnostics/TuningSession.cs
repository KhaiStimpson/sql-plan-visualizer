using SqlPlanViz.Common;
using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics;

/// <summary>Which way the edit went, against the pinned baseline.</summary>
public enum TuningDirection
{
    Unchanged,
    Improved,
    Regressed,
}

/// <summary>
/// The before-and-after that every Phase 5 surface reads from
/// (live-plan-editor-plan.md Phase 5).
///
/// The baseline is pinned rather than "the previous plan", because deltas have to accumulate
/// across many edits: comparing each re-plan with the one before it would show a run of small
/// improvements as a series of small improvements, and never tell you whether you have made
/// the query faster than when you started. Pinning the current plan re-anchors once an
/// improvement is banked.
/// </summary>
public sealed class TuningSession
{
    /// <summary>Cost changes smaller than this are rounding, not results.</summary>
    private const double MaterialCostFraction = 0.02;

    public PlanStatement? Baseline { get; private set; }

    public PlanStatement? Current { get; private set; }

    public PlanDiffResult? Diff { get; private set; }

    /// <summary>Set when the editor's text has moved on from the plan on screen.</summary>
    public bool IsStale { get; set; }

    public event EventHandler? Changed;

    public bool HasBaseline => Baseline is not null;

    public bool HasComparison => Baseline is not null && Current is not null && !ReferenceEquals(Baseline, Current);

    public double BaselineCost => Baseline?.Summary.TotalSubtreeCost ?? 0;

    public double CurrentCost => Current?.Summary.TotalSubtreeCost ?? 0;

    public double CostDelta => CurrentCost - BaselineCost;

    /// <summary>Signed fraction of the baseline. Zero when there is no baseline cost to divide by.</summary>
    public double CostFraction => BaselineCost > 0 ? CostDelta / BaselineCost : 0;

    /// <summary>True when neither side has runtime stats, so every number here is the optimizer's opinion.</summary>
    public bool IsEstimatedOnly => Current?.HasRuntimeStats != true;

    public TuningDirection Direction
    {
        get
        {
            if (!HasComparison || Math.Abs(CostFraction) < MaterialCostFraction)
            {
                return TuningDirection.Unchanged;
            }

            return CostDelta < 0 ? TuningDirection.Improved : TuningDirection.Regressed;
        }
    }

    /// <summary>
    /// Sets the plan now on screen. The first one pins itself as the baseline — the session
    /// starts from whatever you opened, with no ceremony.
    /// </summary>
    public void SetCurrent(PlanStatement? statement)
    {
        Current = statement;
        Baseline ??= statement;
        Recompute();
    }

    /// <summary>Re-anchors the comparison to the plan on screen.</summary>
    public void PinCurrent()
    {
        Baseline = Current;
        Recompute();
    }

    public void Reset()
    {
        Baseline = null;
        Current = null;
        Diff = null;
        IsStale = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Recompute()
    {
        Diff = HasComparison ? PlanDiff.Compare(Baseline!, Current!) : null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The shape changes worth naming in the bar. A cost number says how much; this says what,
    /// which is the part you can act on.
    /// </summary>
    public IReadOnlyList<string> ShapeChanges
    {
        get
        {
            if (Diff is not { } diff)
            {
                return [];
            }

            var added = diff.Nodes.Where(n => n.Kind == PlanDiffKind.Added && n.After is not null).ToList();
            var removed = diff.Nodes.Where(n => n.Kind == PlanDiffKind.Removed && n.Before is not null).ToList();
            var described = new List<string>();
            var usedAdded = new HashSet<PlanNodeDelta>();
            var usedRemoved = new HashSet<PlanNodeDelta>();

            // A scan replacing a seek on the same table is one change, not two, and saying so
            // is the difference between a useful headline and a list of operator names.
            foreach (var gone in removed)
            {
                var target = Normalize(gone.Before!.ObjectName);
                if (target.Length == 0)
                {
                    continue;
                }

                var replacement = added.FirstOrDefault(a =>
                    !usedAdded.Contains(a)
                    && Normalize(a.After!.ObjectName) == target
                    && a.After!.PhysicalOp != gone.Before!.PhysicalOp);

                if (replacement is null)
                {
                    continue;
                }

                usedAdded.Add(replacement);
                usedRemoved.Add(gone);
                described.Add($"{gone.Before!.PhysicalOp} → {replacement.After!.PhysicalOp} on {Short(gone.Before!.ObjectName)}");
            }

            foreach (var group in added.Where(a => !usedAdded.Contains(a)).GroupBy(a => a.After!.PhysicalOp))
            {
                described.Add(group.Count() == 1
                    ? $"{group.Key} added"
                    : $"{group.Count()} × {group.Key} added");
            }

            foreach (var group in removed.Where(r => !usedRemoved.Contains(r)).GroupBy(r => r.Before!.PhysicalOp))
            {
                described.Add(group.Count() == 1
                    ? $"{group.Key} removed"
                    : $"{group.Count()} × {group.Key} removed");
            }

            return described;
        }
    }

    /// <summary>The bar's first line: direction and magnitude, or why there is nothing to say yet.</summary>
    public string Headline
    {
        get
        {
            if (Current is null)
            {
                return "No plan loaded.";
            }

            if (!HasComparison)
            {
                return $"Baseline pinned  ·  estimated cost {Format.Cost(CurrentCost)}";
            }

            var arrow = Direction switch
            {
                TuningDirection.Improved => "↓",
                TuningDirection.Regressed => "↑",
                _ => "→",
            };

            var percent = BaselineCost > 0
                ? $"  {arrow} {Math.Abs(CostFraction) * 100:0.#}%"
                : string.Empty;

            var verdict = Direction switch
            {
                TuningDirection.Improved => "Better",
                TuningDirection.Regressed => "Worse",
                _ => "No material change",
            };

            return $"{verdict}  ·  {Format.Cost(BaselineCost)} → {Format.Cost(CurrentCost)}{percent}";
        }
    }

    /// <summary>
    /// The bar's second line. It always names its unit, because a fall in estimated subtree
    /// cost is the optimizer's opinion and not a measured improvement — the plan's own risk
    /// section, restated where someone will read it.
    /// </summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();

            if (IsStale)
            {
                parts.Add("Edited since this plan was captured — press Ctrl+Enter to re-plan");
            }

            var shapes = ShapeChanges;
            if (shapes.Count > 0)
            {
                parts.Add(string.Join("  ·  ", shapes.Take(3)));
                if (shapes.Count > 3)
                {
                    parts.Add($"+{shapes.Count - 3} more");
                }
            }
            else if (HasComparison)
            {
                parts.Add("Same plan shape, different numbers");
            }

            parts.Add(IsEstimatedOnly
                ? "Estimated cost — the optimizer's opinion, not a measurement"
                : "Measured from an actual plan");

            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>True when the plans differ in shape and not merely in metrics.</summary>
    public bool ShapeChanged => Baseline is not null
                               && Current is not null
                               && !string.Equals(Baseline.Fingerprint, Current.Fingerprint, StringComparison.Ordinal);

    private static string Normalize(string? objectName) =>
        (objectName ?? string.Empty).Replace("[", string.Empty).Replace("]", string.Empty).ToUpperInvariant();

    /// <summary>
    /// The table's own name, as the query writes it — Showplan gives
    /// "schema.table AS alias.index", and the headline wants "Orders".
    /// </summary>
    private static string Short(string? objectName)
    {
        var text = objectName ?? string.Empty;
        var asIndex = text.IndexOf(" AS ", StringComparison.OrdinalIgnoreCase);
        if (asIndex >= 0)
        {
            text = text[..asIndex];
        }

        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return (parts.Length == 0 ? text : parts[^1]).Trim('[', ']');
    }
}
