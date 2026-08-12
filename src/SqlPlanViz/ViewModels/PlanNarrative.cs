using SqlPlanViz.Common;
using SqlPlanViz.Diagnostics;
using SqlPlanViz.Model;
using System.Text;

namespace SqlPlanViz.ViewModels;

/// <summary>Builds a short, paste-ready overview from the statement's ranked diagnostics.</summary>
public static class PlanNarrative
{
    public static string Generate(PlanStatement? statement, ExplanationVerbosity verbosity = ExplanationVerbosity.Expansive)
    {
        if (statement is null)
        {
            return string.Empty;
        }

        var opening = statement.Summary.QueryElapsedMs is double elapsed
            ? $"This query completed in {Format.Milliseconds(elapsed)}"
            : $"This estimated plan contains {statement.AllNodes.Count} operators";

        if (statement.Findings.Count == 0)
        {
            return $"{opening}. The diagnostic rules found no specific tuning problem in the available plan data.";
        }

        var primary = statement.Findings[0];
        if (verbosity == ExplanationVerbosity.Terse)
        {
            return $"{opening}. {TrimSentence(primary.Title)}.";
        }
        var impact = primary.ImpactFraction > 0
            ? $" It represents about {Format.Percent(primary.ImpactFraction)} of the plan's measured impact."
            : string.Empty;
        var nextStep = primary.Fixes.FirstOrDefault() is { } fix
            ? $" Suggested next step: {TrimSentence(fix.Summary)}."
            : string.Empty;

        var supporting = statement.Findings.Skip(1).FirstOrDefault();
        var context = supporting is null
            ? string.Empty
            : $" The next strongest signal is {LowercaseFirst(TrimSentence(supporting.Title))}.";

        return $"{opening}. {TrimSentence(primary.Title)}.{impact}{context}{nextStep}";
    }

    public static string GenerateMarkdown(PlanStatement statement, ExplanationVerbosity verbosity = ExplanationVerbosity.Expansive)
    {
        var markdown = new StringBuilder();
        markdown.AppendLine("## SQL plan diagnosis");
        markdown.AppendLine();
        markdown.AppendLine(Generate(statement, verbosity));
        markdown.AppendLine();
        markdown.AppendLine($"- Operators: {statement.AllNodes.Count}");
        markdown.AppendLine($"- Estimated subtree cost: {Format.Cost(statement.Summary.TotalSubtreeCost)}");
        if (statement.Summary.QueryElapsedMs is double elapsed)
        {
            markdown.AppendLine($"- Elapsed: {Format.Milliseconds(elapsed)}");
        }

        if (statement.Summary.QueryCpuMs is double cpu)
        {
            markdown.AppendLine($"- CPU: {Format.Milliseconds(cpu)}");
        }

        foreach (var finding in statement.Findings)
        {
            markdown.AppendLine();
            markdown.AppendLine($"### {finding.Title}");
            markdown.AppendLine();
            markdown.AppendLine($"**{finding.Severity} · {finding.Confidence} confidence · {Format.Percent(finding.ImpactFraction)} impact**");
            markdown.AppendLine();
            markdown.AppendLine(verbosity == ExplanationVerbosity.Terse ? FirstSentence(finding.Why) : finding.Why);

            foreach (var fix in finding.Fixes)
            {
                markdown.AppendLine();
                markdown.AppendLine($"- **{fix.Kind}:** {fix.Summary}");
                if (!string.IsNullOrWhiteSpace(fix.Caveat))
                {
                    markdown.AppendLine($"  - Caveat: {fix.Caveat}");
                }

                if (!string.IsNullOrWhiteSpace(fix.Snippet))
                {
                    markdown.AppendLine();
                    markdown.AppendLine("```sql");
                    markdown.AppendLine(fix.Snippet.Trim());
                    markdown.AppendLine("```");
                }
            }
        }

        return markdown.ToString().TrimEnd();
    }

    private static string TrimSentence(string value) => value.Trim().TrimEnd('.', '!', '?');

    private static string LowercaseFirst(string value) => string.IsNullOrEmpty(value)
        ? value
        : char.ToLowerInvariant(value[0]) + value[1..];

    internal static string FirstSentence(string value)
    {
        var end = value.IndexOfAny(['.', '!', '?']);
        return end < 0 ? value : value[..(end + 1)];
    }
}
