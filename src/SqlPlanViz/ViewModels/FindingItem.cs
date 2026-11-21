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
        Fixes = finding.Fixes.Select((f, i) => new FindingFixItem(f, i + 1)).ToList();
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

    /// <summary>The row's title: the anti-pattern's name, sentence case ("Tempdb spill").</summary>
    public string Name => AntiPattern.Name.Length > 1
        ? AntiPattern.Name[0] + AntiPattern.Name[1..].ToLowerInvariant()
        : AntiPattern.Name;

    /// <summary>The row's description: this occurrence ("Sort spilled to tempdb").</summary>
    public string Description => Finding.Title;

    /// <summary>The severity as a word, never colour alone.</summary>
    public string SeverityWord => Finding.Severity switch
    {
        FindingSeverity.Critical => "Critical",
        FindingSeverity.Warning => "Warning",
        _ => "Info",
    };

    /// <summary>The impact, right-aligned in the row: "100%", or "Context" when it carries none.</summary>
    public string ImpactShort => Finding.ImpactFraction > 0 ? Format.Percent(Finding.ImpactFraction) : "Context";

    public string ConfidenceLower => $"{Finding.Confidence} confidence".ToLowerInvariant();

    /// <summary>The entity link to the implicated operator(s): "Sort · node 0".</summary>
    public string NodeEntity => Finding.Nodes.Count switch
    {
        0 => "Statement",
        1 => $"{Finding.Nodes[0].PhysicalOp} · node {Finding.Nodes[0].NodeId}",
        _ => $"{Finding.Nodes.Count} related operators",
    };

    /// <summary>The selected finding: drawn as a selected record and expanded inline.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Re-evaluates the theme-dependent brushes bound to this row.</summary>
    public void RefreshTheme() => OnPropertyChanged(nameof(IsSelected));

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
    [NotifyPropertyChangedFor(nameof(TriageWord))]
    [NotifyPropertyChangedFor(nameof(IsTried))]
    [NotifyPropertyChangedFor(nameof(IsDidNotHelp))]
    [NotifyPropertyChangedFor(nameof(IsFixed))]
    private FixTriageState _triageState;

    /// <summary>The triage state as the row's trailing word; empty until something was tried.</summary>
    public string TriageWord => TriageState switch
    {
        FixTriageState.Tried => "Tried",
        FixTriageState.DidNotHelp => "No help",
        FixTriageState.Fixed => "Fixed",
        _ => string.Empty,
    };

    public bool IsTried => TriageState == FixTriageState.Tried;

    public bool IsDidNotHelp => TriageState == FixTriageState.DidNotHelp;

    public bool IsFixed => TriageState == FixTriageState.Fixed;

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
    public FindingFixItem(Fix fix, int number)
    {
        Fix = fix;
        Number = $"{number}.";
    }

    public Fix Fix { get; }

    /// <summary>"1." — fixes are listed most effective first.</summary>
    public string Number { get; }

    public string Kind => Fix.Kind.ToString();

    public string Summary => Fix.Summary;

    public string Snippet => Fix.Snippet ?? string.Empty;

    public string Caveat => Fix.Caveat ?? string.Empty;

    public bool HasSnippet => !string.IsNullOrWhiteSpace(Fix.Snippet);

    public bool HasCaveat => !string.IsNullOrWhiteSpace(Fix.Caveat);
}
