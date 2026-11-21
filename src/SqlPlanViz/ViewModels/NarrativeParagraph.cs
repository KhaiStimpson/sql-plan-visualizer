using System.Text.RegularExpressions;
using SqlPlanViz.Common;
using SqlPlanViz.Diagnostics;
using SqlPlanViz.Model;

namespace SqlPlanViz.ViewModels;

/// <summary>How a run of narrative text is set: plain, bold (a metric or lead), mono (an object), or an operator link.</summary>
public enum NarrativeRunKind
{
    Plain,
    Bold,
    Mono,
    Entity,
}

public sealed record NarrativeRun(string Text, NarrativeRunKind Kind, PlanNode? Node = null);

public sealed record NarrativeParagraph(IReadOnlyList<NarrativeRun> Runs);

/// <summary>
/// The Diagnosis callout as structured paragraphs (docs/gauge-design-system-plan.md Phase 7):
/// operator names become entity links back to the canvas, metrics are bold, object names are
/// mono, and the next step leads with a bold "Next step:". <see cref="PlanNarrative.Generate"/>
/// stays the plain-text form for copy and export.
/// </summary>
public static partial class NarrativeBuilder
{
    public static IReadOnlyList<NarrativeParagraph> Build(PlanStatement? statement, ExplanationVerbosity verbosity)
    {
        if (statement is null)
        {
            return [];
        }

        var paragraphs = new List<NarrativeParagraph>();
        var opening = new List<NarrativeRun>();
        if (statement.Summary.QueryElapsedMs is double elapsed)
        {
            opening.Add(new("Completed in ", NarrativeRunKind.Plain));
            opening.Add(new(Format.Milliseconds(elapsed), NarrativeRunKind.Bold));
            opening.Add(new(". ", NarrativeRunKind.Plain));
        }
        else
        {
            opening.Add(new($"Estimated plan with {statement.AllNodes.Count} operators. ", NarrativeRunKind.Plain));
        }

        if (statement.Findings.Count == 0)
        {
            opening.Add(new("The diagnostic rules found no specific tuning problem in the available plan data.", NarrativeRunKind.Plain));
            paragraphs.Add(new(opening));
            return paragraphs;
        }

        var primary = statement.Findings[0];
        opening.AddRange(Linkify(TrimSentence(primary.Title), primary.Nodes));
        if (verbosity == ExplanationVerbosity.Expansive && primary.ImpactFraction > 0)
        {
            opening.Add(new(" and carries about ", NarrativeRunKind.Plain));
            opening.Add(new(Format.Percent(primary.ImpactFraction), NarrativeRunKind.Bold));
            opening.Add(new(" of measured impact.", NarrativeRunKind.Plain));
        }
        else
        {
            opening.Add(new(".", NarrativeRunKind.Plain));
        }

        paragraphs.Add(new(opening));

        if (verbosity == ExplanationVerbosity.Terse)
        {
            return paragraphs;
        }

        if (statement.Findings.Skip(1).FirstOrDefault() is { } supporting)
        {
            var runs = new List<NarrativeRun> { new("Next strongest signal: ", NarrativeRunKind.Plain) };
            runs.AddRange(Linkify(LowercaseFirst(TrimSentence(supporting.Title)), supporting.Nodes));
            runs.Add(new(".", NarrativeRunKind.Plain));
            paragraphs.Add(new(runs));
        }

        if (primary.Fixes.FirstOrDefault() is { } fix)
        {
            var runs = new List<NarrativeRun> { new("Next step:", NarrativeRunKind.Bold), new(" ", NarrativeRunKind.Plain) };
            runs.AddRange(Linkify(LowercaseFirst(TrimSentence(fix.Summary)), primary.Nodes));
            runs.Add(new(".", NarrativeRunKind.Plain));
            paragraphs.Add(new(runs));
        }

        return paragraphs;
    }

    /// <summary>
    /// Splits text into runs: the first mention of an implicated operator becomes a link,
    /// dotted object names go mono, and large counts go bold.
    /// </summary>
    private static IEnumerable<NarrativeRun> Linkify(string text, IReadOnlyList<PlanNode> nodes)
    {
        var marks = new List<(int Start, int Length, NarrativeRunKind Kind, PlanNode? Node)>();

        foreach (var node in nodes.DistinctBy(n => n.PhysicalOp))
        {
            // Rule titles sometimes use the singular ("Nested Loop" for Nested Loops).
            var names = new[] { node.PhysicalOp, node.LogicalOp, node.PhysicalOp.TrimEnd('s') };
            foreach (var name in names.Where(n => !string.IsNullOrEmpty(n)).Distinct())
            {
                var at = text.IndexOf(name, StringComparison.Ordinal);
                if (at >= 0 && !marks.Any(m => Overlaps(m.Start, m.Length, at, name.Length)))
                {
                    marks.Add((at, name.Length, NarrativeRunKind.Entity, node));
                    break;
                }
            }
        }

        foreach (Match match in ObjectName().Matches(text))
        {
            if (!marks.Any(m => Overlaps(m.Start, m.Length, match.Index, match.Length)))
            {
                marks.Add((match.Index, match.Length, NarrativeRunKind.Mono, null));
            }
        }

        foreach (Match match in LargeNumber().Matches(text))
        {
            if (!marks.Any(m => Overlaps(m.Start, m.Length, match.Index, match.Length)))
            {
                marks.Add((match.Index, match.Length, NarrativeRunKind.Bold, null));
            }
        }

        var cursor = 0;
        foreach (var mark in marks.OrderBy(m => m.Start))
        {
            if (mark.Start > cursor)
            {
                yield return new(text[cursor..mark.Start], NarrativeRunKind.Plain);
            }

            yield return new(text.Substring(mark.Start, mark.Length), mark.Kind, mark.Node);
            cursor = mark.Start + mark.Length;
        }

        if (cursor < text.Length)
        {
            yield return new(text[cursor..], NarrativeRunKind.Plain);
        }
    }

    private static bool Overlaps(int aStart, int aLength, int bStart, int bLength) =>
        aStart < bStart + bLength && bStart < aStart + aLength;

    private static string TrimSentence(string value) => value.Trim().TrimEnd('.', '!', '?');

    private static string LowercaseFirst(string value) =>
        string.IsNullOrEmpty(value) || (value.Length > 1 && char.IsUpper(value[1]))
            ? value
            : char.ToLowerInvariant(value[0]) + value[1..];

    /// <summary>dbo.Orders, o.PK_Orders, dbo.Orders AS o: schema-qualified names.</summary>
    [GeneratedRegex(@"\b[A-Za-z_][\w]*\.[A-Za-z_][\w]*(\.[A-Za-z_][\w]*)?\b")]
    private static partial Regex ObjectName();

    /// <summary>50,000 or 10000x: counts large enough to be the point of the sentence.</summary>
    [GeneratedRegex(@"\b\d{1,3}(,\d{3})+\b|\b\d{4,}\b")]
    private static partial Regex LargeNumber();
}
