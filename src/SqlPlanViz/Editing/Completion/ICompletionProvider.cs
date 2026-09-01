namespace SqlPlanViz.Editing.Completion;

/// <summary>
/// One source of completions. The plan wants four of these — keywords, the loaded plan, the
/// live catalog and the tuning layer — each degrading independently, so a dead connection
/// costs you the catalog and nothing else.
/// </summary>
public interface ICompletionProvider
{
    /// <summary>Stable id, used to disable a provider and to attribute its items.</summary>
    string Id { get; }

    /// <summary>Human-readable name for a settings toggle.</summary>
    string DisplayName { get; }

    bool IsEnabled { get; set; }

    /// <summary>
    /// Unfiltered candidates for this context. Ranking and prefix filtering belong to the
    /// engine — a provider that filtered itself could not be re-queried as the user types
    /// without going back to its source.
    /// </summary>
    IEnumerable<CompletionItem> GetItems(CompletionContext context);
}
