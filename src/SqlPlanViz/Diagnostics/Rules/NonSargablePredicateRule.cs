using System.Text.RegularExpressions;
using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics.Rules;

/// <summary>
/// Regexes a node's <see cref="PlanNode.Predicate"/>/<see cref="PlanNode.SeekPredicate"/> for
/// a function wrapping a column — <c>YEAR(x)</c>, <c>LEFT(x,…)</c>, <c>CONVERT(…, x)</c>,
/// <c>ISNULL(x,…)</c>, <c>x + ''</c>, leading-wildcard <c>LIKE '%…'</c> — any of which
/// prevents an index seek on that column (tuning-roadmap.md Phase 2.9).
/// </summary>
public sealed class NonSargablePredicateRule : IPlanRule
{
    private static readonly (Regex Pattern, string Kind, Func<Match, string> Rewrite)[] Detectors =
    [
        (new Regex(@"YEAR\(\s*(?<col>[\w.\[\]]+)\s*\)\s*=\s*(?<val>\S+)", RegexOptions.IgnoreCase),
            "YEAR() wrapping a column",
            m => $"{m.Groups["col"].Value} >= '<{m.Groups["val"].Value}>-01-01' AND {m.Groups["col"].Value} < '<{m.Groups["val"].Value}+1>-01-01'"),

        (new Regex(@"LEFT\(\s*(?<col>[\w.\[\]]+)\s*,\s*\d+\s*\)\s*=\s*(?<val>\S+)", RegexOptions.IgnoreCase),
            "LEFT() wrapping a column",
            m => $"{m.Groups["col"].Value} LIKE '{TrimQuote(m.Groups["val"].Value)}%'"),

        (new Regex(@"CONVERT(?:_IMPLICIT)?\(\s*[\w()]+\s*,\s*(?<col>[\w.\[\]]+)", RegexOptions.IgnoreCase),
            "implicit/explicit CONVERT() wrapping a column",
            m => $"{m.Groups["col"].Value} (matched against the parameter's native type, no CONVERT)"),

        (new Regex(@"ISNULL\(\s*(?<col>[\w.\[\]]+)\s*,", RegexOptions.IgnoreCase),
            "ISNULL() wrapping a column",
            m => $"{m.Groups["col"].Value} = @value OR {m.Groups["col"].Value} IS NULL"),

        (new Regex(@"(?<col>[\w.\[\]]+)\s*\+\s*''", RegexOptions.IgnoreCase),
            "string concatenation on a column",
            m => $"{m.Groups["col"].Value} (drop the + '' concatenation)"),

        (new Regex(@"LIKE\s+'%", RegexOptions.IgnoreCase),
            "leading-wildcard LIKE",
            _ => "a range predicate, or full-text search if wildcard prefix search is required"),
    ];

    public string RuleId => "non-sargable-predicate";

    public IEnumerable<PlanFinding> Analyse(PlanStatement statement)
    {
        var totalCost = statement.Summary.TotalSubtreeCost;

        foreach (var node in statement.AllNodes)
        {
            var predicate = node.Predicate ?? node.SeekPredicate;
            if (string.IsNullOrEmpty(predicate))
            {
                continue;
            }

            foreach (var (pattern, kind, rewrite) in Detectors)
            {
                var match = pattern.Match(predicate);
                if (!match.Success)
                {
                    continue;
                }

                var impact = totalCost > 0 ? Math.Clamp(node.EstimatedSubtreeCost / totalCost, 0, 1) : 0;
                var target = node.ObjectName ?? node.PhysicalOp;

                yield return new PlanFinding
                {
                    RuleId = RuleId,
                    Title = $"Non-sargable predicate on {target} — {kind}",
                    Severity = FindingSeverity.Warning,
                    Confidence = FindingConfidence.Likely,
                    Nodes = [node],
                    Why = $"The predicate on {target} has {kind}, which prevents an index seek on that "
                          + $"column — SQL Server must evaluate the function for every row instead. Predicate: {predicate}",
                    Fixes =
                    [
                        new Fix(
                            "Rewrite the predicate so the column is bare and the index can be seeked.",
                            $"Before: {predicate}{Environment.NewLine}After:  {rewrite(match)}",
                            null,
                            FixKind.Rewrite),
                    ],
                    ImpactFraction = impact,
                };

                // One finding per node is enough — the first matching pattern explains it.
                break;
            }
        }
    }

    private static string TrimQuote(string value) => value.Trim('\'');
}
