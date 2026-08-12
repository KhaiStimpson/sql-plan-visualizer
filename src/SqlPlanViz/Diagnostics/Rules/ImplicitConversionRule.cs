using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics.Rules;

/// <summary>
/// A <c>PlanAffectingConvert</c> warning (tuning-roadmap.md Phase 2.7): SQL Server had to
/// convert a column's type to evaluate a predicate, which can silently disable an index seek.
/// The warning detail already carries the column and both types — extracted here for a
/// concrete title instead of a generic blurb.
/// </summary>
public sealed class ImplicitConversionRule : IPlanRule
{
    public string RuleId => "implicit-conversion";

    public IEnumerable<PlanFinding> Analyse(PlanStatement statement)
    {
        var totalCost = statement.Summary.TotalSubtreeCost;

        foreach (var node in statement.AllNodes)
        {
            var warning = node.Warnings.FirstOrDefault(w => w.Type == "PlanAffectingConvert");
            if (warning is null)
            {
                continue;
            }

            var (expression, issue) = ParseDetail(warning.Detail);
            var impact = totalCost > 0 ? Math.Clamp(node.EstimatedSubtreeCost / totalCost, 0, 1) : 0;
            var target = node.ObjectName ?? node.PhysicalOp;

            yield return new PlanFinding
            {
                RuleId = RuleId,
                Title = string.IsNullOrEmpty(expression)
                    ? $"Implicit conversion on {target}"
                    : $"Implicit conversion on {expression}",
                Severity = FindingSeverity.Warning,
                Confidence = FindingConfidence.High,
                Nodes = [node],
                Why = "SQL Server had to convert a column's type to evaluate a predicate here"
                      + (string.IsNullOrEmpty(issue) ? string.Empty : $" ({issue})")
                      + (string.IsNullOrEmpty(expression) ? string.Empty : $": {expression}")
                      + ". A type mismatch like this can silently turn a seek into a scan, "
                      + "because the index is built on the original type.",
                Fixes =
                [
                    new Fix(
                        "Match the parameter or literal's type to the column's declared type.",
                        null,
                        null,
                        FixKind.Rewrite),
                    new Fix(
                        "Alternatively, change the column's type to match how it's queried.",
                        null,
                        "Changing a column type is a schema migration — plan for downtime/locking and dependent objects.",
                        FixKind.Rewrite),
                ],
                ImpactFraction = impact,
            };
        }
    }

    private static (string? Expression, string? Issue) ParseDetail(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return (null, null);
        }

        // Detail is formatted as "{ConvertIssue}: {Expression}" by ShowplanParser, e.g.
        // "Seek Plan: CONVERT_IMPLICIT(nvarchar(4000),[dbo].[Orders].[OrderId],0)=@p1".
        var parts = detail.Split(':', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? (parts[1], parts[0]) : (detail, null);
    }
}
