using CommunityToolkit.Mvvm.ComponentModel;
using SqlPlanViz.Common;
using SqlPlanViz.Diagnostics;
using SqlPlanViz.Model;

namespace SqlPlanViz.ViewModels;

public sealed partial class FindingItem : ObservableObject
{
    public FindingItem(PlanFinding finding, PlanStatement statement, ExplanationVerbosity verbosity, FixTriageState triageState, bool canPersist)
    {
        Finding = finding;
        Fixes = finding.Fixes.Select(f => new FindingFixItem(f)).ToList();
        Why = verbosity == ExplanationVerbosity.Terse
            ? PlanNarrative.FirstSentence(finding.Why)
            : finding.Why;
        WhatIf = WhatIfEstimator.Estimate(statement, finding);
        TriageState = triageState;
        CanPersist = canPersist;
    }

    public PlanFinding Finding { get; }

    public AntiPatternInfo AntiPattern => AntiPatternLibrary.For(Finding.RuleId);

    public string Title => $"{AntiPattern.Name}: {Finding.Title}";

    public FindingSeverity Severity => Finding.Severity;

    public string SeverityText => Finding.Severity.ToString();

    public string ConfidenceText => $"{Finding.Confidence} confidence";

    public string ImpactText => Finding.ImpactFraction > 0
        ? $"{Format.Percent(Finding.ImpactFraction)} impact"
        : "Context";

    public string Why { get; }

    public PlanNode? PrimaryNode => Finding.Nodes.FirstOrDefault();

    public string Location => Finding.Nodes.Count switch
    {
        0 => "Statement-level finding",
        1 => $"Node {Finding.Nodes[0].NodeId} · {Finding.Nodes[0].PhysicalOp}",
        _ => $"{Finding.Nodes.Count} related operators",
    };

    public string SeverityGlyph => Finding.Severity switch
    {
        FindingSeverity.Critical => "\uEA39",
        FindingSeverity.Warning => "\uE7BA",
        _ => "\uE946",
    };

    public IReadOnlyList<FindingFixItem> Fixes { get; }

    public bool HasFixes => Fixes.Count > 0;

    public WhatIfEstimate? WhatIf { get; }

    public bool HasWhatIf => WhatIf is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TriageText))]
    private FixTriageState _triageState;

    public string TriageText => TriageState switch
    {
        FixTriageState.Tried => "Tried",
        FixTriageState.DidNotHelp => "Did not help",
        FixTriageState.Fixed => "Fixed",
        _ => "Not tried",
    };

    public string PersistenceKey => CreatePersistenceKey(Finding);

    public bool CanPersist { get; }

    public static string CreatePersistenceKey(PlanFinding finding) =>
        $"{finding.RuleId}:{string.Join(',', finding.Nodes.Select(node => node.NodeId).Order())}";
}

public sealed class FindingFixItem
{
    public FindingFixItem(Fix fix) => Fix = fix;

    public Fix Fix { get; }

    public string Kind => Fix.Kind.ToString();

    public string Summary => Fix.Summary;

    public string Snippet => Fix.Snippet ?? string.Empty;

    public string Caveat => Fix.Caveat ?? string.Empty;

    public bool HasSnippet => !string.IsNullOrWhiteSpace(Fix.Snippet);

    public bool HasCaveat => !string.IsNullOrWhiteSpace(Fix.Caveat);
}
