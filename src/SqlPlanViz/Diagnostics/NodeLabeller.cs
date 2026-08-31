using System.Text.RegularExpressions;
using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics;

/// <summary>
/// Derives human-readable labels for a <see cref="PlanNode"/> that answer *what* it touches
/// and *how* — pure functions over the parsed model, no UI references (hot-path-plan.md
/// Phase 1). Every method here is best-effort: a shape it cannot describe returns null and
/// the caller falls back to <c>ObjectName ?? LogicalOp</c>.
/// </summary>
public static class NodeLabeller
{
    private const string JoinGlyph = " ⋈ ";

    /// <summary>
    /// For a node with no <see cref="PlanNode.ObjectTable"/> of its own (a join, set operator,
    /// or similar), names what each input touches by walking down to the nearest
    /// object-bearing descendant of each child. Ambiguous single-purpose shapes — Spool,
    /// Exchange/Parallelism, Concatenation, Compute Scalar — never appear in the result
    /// because they have no object of their own, so the walk passes straight through them to
    /// whatever they wrap.
    /// </summary>
    public static string? DescribeSources(PlanNode node)
    {
        if (!string.IsNullOrEmpty(node.ObjectTable))
        {
            return null;
        }

        var sources = node.Children
            .Select(NearestObjectBearingTable)
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToList();

        return sources.Count switch
        {
            0 => null,
            1 => sources[0],
            2 => sources[0] + JoinGlyph + sources[1],
            _ => $"{sources.Count} sources",
        };
    }

    private static string? NearestObjectBearingTable(PlanNode node)
    {
        if (!string.IsNullOrEmpty(node.ObjectTable))
        {
            return node.ObjectTable;
        }

        foreach (var child in node.Children)
        {
            var found = NearestObjectBearingTable(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static readonly Regex BracketedSegment = new(@"\[([^\[\]]+)\]", RegexOptions.Compiled);

    /// <summary>
    /// Extracts the join column(s) from a join node's <see cref="PlanNode.Predicate"/>, in
    /// short form (<c>on CustomerId</c>, or <c>on CustomerId = ParentCustomerId</c> when the
    /// column names differ). Returns null for non-join nodes, missing predicates, or any
    /// predicate shape this does not confidently parse — never a guess. In practice most
    /// equi-joins carry their key on a child operator's SeekPredicate/HashKeys rather than
    /// the join node's own Predicate, so null here is common and expected, not a bug.
    /// </summary>
    public static string? DescribeJoinKeys(PlanNode node)
    {
        if (!LooksLikeJoin(node) || string.IsNullOrEmpty(node.Predicate))
        {
            return null;
        }

        var predicate = node.Predicate;

        // Multiple conditions are ambiguous to summarise in one clause; skip rather than guess.
        if (predicate.Contains(" AND ", StringComparison.OrdinalIgnoreCase)
            || predicate.Contains(" OR ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var eq = predicate.IndexOf('=');
        if (eq <= 0 || eq >= predicate.Length - 1)
        {
            return null;
        }

        // Reject <=, >=, <>, != — this only speaks for plain equality.
        if (predicate[eq - 1] is '<' or '>' or '!')
        {
            return null;
        }

        var rightStart = predicate[eq + 1] == '=' ? eq + 2 : eq + 1;
        var left = LastBracketedSegment(predicate[..eq]);
        var right = LastBracketedSegment(predicate[rightStart..]);
        if (left is null || right is null)
        {
            return null;
        }

        return left == right ? $"on {left}" : $"on {left} = {right}";
    }

    private static string? LastBracketedSegment(string s)
    {
        var matches = BracketedSegment.Matches(s);
        return matches.Count == 0 ? null : matches[^1].Groups[1].Value;
    }

    private static bool LooksLikeJoin(PlanNode node) =>
        node.LogicalOp.Contains("Join", StringComparison.OrdinalIgnoreCase)
        || node.PhysicalOp.Contains("Join", StringComparison.OrdinalIgnoreCase)
        || node.PhysicalOp.Contains("Nested Loops", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Truncates a long object name from the left so the distinguishing suffix (the index, or
    /// the alias) survives, e.g. <c>dbo.Orders AS o.PK_Orders</c> → <c>…Orders AS o.PK_Orders</c>.
    /// </summary>
    public static string TruncateObjectName(string objectName, int maxLength = 32)
    {
        if (objectName.Length <= maxLength || maxLength <= 1)
        {
            return objectName;
        }

        return "…" + objectName[^(maxLength - 1)..];
    }
}
