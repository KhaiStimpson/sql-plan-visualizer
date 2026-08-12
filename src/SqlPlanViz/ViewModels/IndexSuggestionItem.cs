using CommunityToolkit.Mvvm.ComponentModel;
using SqlPlanViz.Capture;
using SqlPlanViz.Model;

namespace SqlPlanViz.ViewModels;

public sealed partial class IndexSuggestionItem : ObservableObject
{
    public IndexSuggestionItem(MissingIndexSuggestion suggestion) => Suggestion = suggestion;

    public MissingIndexSuggestion Suggestion { get; }

    public string DisplayTarget => Suggestion.DisplayTarget;
    public string ImpactText => Suggestion.ImpactText;
    public string ColumnSummary => Suggestion.ColumnSummary;
    public string SuggestedCreateStatement => Suggestion.SuggestedCreateStatement;
    public string Caveat => "A new index adds storage and write-maintenance cost; validate the workload before deployment.";

    [ObservableProperty]
    private string _verificationText = "Waiting for live index verification.";

    [ObservableProperty]
    private bool _canShowScript;

    [ObservableProperty]
    private bool _isChecking;

    public void MarkUnavailable(string message)
    {
        IsChecking = false;
        CanShowScript = false;
        VerificationText = message;
    }

    public void ApplyVerification(DatabaseObjectContext? context)
    {
        IsChecking = false;
        if (context is null)
        {
            MarkUnavailable("The suggested table was not found in the connected database; DDL is hidden.");
            return;
        }

        if (context.RowCount < 1000)
        {
            MarkUnavailable($"Verified table size: only {context.RowCount:N0} rows. A new index is unlikely to beat a scan, so DDL is hidden.");
            return;
        }

        var proposedKeys = Suggestion.EqualityColumns.Concat(Suggestion.InequalityColumns).Select(Normalize).ToList();
        var proposedIncludes = Suggestion.IncludedColumns.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var similar = context.Indexes.FirstOrDefault(index =>
            proposedKeys.Count > 0
            && index.KeyColumns.Select(Normalize).Take(proposedKeys.Count).SequenceEqual(proposedKeys, StringComparer.OrdinalIgnoreCase)
            && proposedIncludes.IsSubsetOf(index.KeyColumns.Concat(index.IncludedColumns).Select(Normalize)));

        if (similar is not null)
        {
            MarkUnavailable($"Existing index {similar.Name} already covers these keys and includes; duplicate DDL is hidden.");
            return;
        }

        CanShowScript = true;
        VerificationText = $"Verified against {context.Indexes.Count} existing indexes on {context.RowCount:N0} rows. No covering equivalent was found.";
    }

    private static string Normalize(string value) => value.Split('.').Last().Trim('[', ']').Trim();
}
