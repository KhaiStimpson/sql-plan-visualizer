namespace SqlPlanViz.Editing.Completion;

/// <summary>
/// Fans out to the registered providers, merges what they return, filters against what the
/// user has typed and ranks the result (live-plan-editor-plan.md Phase 2).
///
/// Providers know nothing about ranking or filtering, so re-querying as characters arrive is
/// free: the candidate set is cached against the caret position it was built for and only
/// re-filtered until the caret leaves that word.
/// </summary>
public sealed class CompletionEngine
{
    /// <summary>Above this, the list is noise — no one scrolls a completion popup.</summary>
    public const int MaxItems = 60;

    private readonly List<ICompletionProvider> _providers = [];

    public IReadOnlyList<ICompletionProvider> Providers => _providers;

    public void Register(ICompletionProvider provider)
    {
        _providers.RemoveAll(p => p.Id == provider.Id);
        _providers.Add(provider);
    }

    public void Remove(string providerId) => _providers.RemoveAll(p => p.Id == providerId);

    public ICompletionProvider? Find(string providerId) =>
        _providers.FirstOrDefault(p => p.Id == providerId);

    /// <summary>
    /// The ranked list for a context. Returns empty when the caret is somewhere completion
    /// must not fire — inside a string or a comment, or mid-word with nothing typed and no
    /// explicit invoke.
    /// </summary>
    public IReadOnlyList<CompletionItem> GetCompletions(CompletionContext context)
    {
        if (context.IsInLiteralOrComment)
        {
            return [];
        }

        if (!context.ExplicitlyInvoked && context.Prefix.Length == 0 && !context.IsAfterDot)
        {
            return [];
        }

        var candidates = new List<CompletionItem>();
        foreach (var provider in _providers)
        {
            if (!provider.IsEnabled)
            {
                continue;
            }

            try
            {
                candidates.AddRange(provider.GetItems(context));
            }
            catch (Exception)
            {
                // Each provider degrades independently, per the plan. One throwing must not
                // take the list — or the keystroke that opened it — down with it.
            }
        }

        var matched = new List<CompletionItem>(candidates.Count);
        foreach (var item in candidates)
        {
            var match = Match(item.Label, context.Prefix);
            if (match == CompletionMatchKind.None)
            {
                continue;
            }

            item.MatchKind = match;
            matched.Add(item);
        }

        return matched
            .GroupBy(i => (i.Label, i.Kind))
            .Select(g => g.OrderBy(i => i.SortRank).First())
            .OrderBy(i => i.MatchKind)
            .ThenBy(i => i.SortRank)
            .ThenBy(i => i.Label.Length)
            .ThenBy(i => i.Label, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxItems)
            .ToList();
    }

    /// <summary>
    /// Prefix beats initials beats substring beats subsequence — the ordering the plan asks
    /// for, extended with the initials case because SQL names are relentlessly CamelCase and
    /// "oli" meaning OrderLineItems is worth more than a stray substring hit.
    /// </summary>
    public static CompletionMatchKind Match(string label, string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return CompletionMatchKind.Prefix;
        }

        if (string.Equals(label, prefix, StringComparison.OrdinalIgnoreCase))
        {
            return CompletionMatchKind.Exact;
        }

        if (label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return CompletionMatchKind.Prefix;
        }

        if (MatchesInitials(label, prefix))
        {
            return CompletionMatchKind.Initials;
        }

        if (label.Contains(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return CompletionMatchKind.Substring;
        }

        return IsSubsequence(label, prefix) ? CompletionMatchKind.Subsequence : CompletionMatchKind.None;
    }

    private static bool MatchesInitials(string label, string prefix)
    {
        var initials = new System.Text.StringBuilder();
        for (var i = 0; i < label.Length; i++)
        {
            var c = label[i];
            var isBoundary = i == 0
                             || (char.IsUpper(c) && !char.IsUpper(label[i - 1]))
                             || label[i - 1] is '_' or '.' or ' ';
            if (isBoundary && char.IsLetterOrDigit(c))
            {
                initials.Append(char.ToUpperInvariant(c));
            }
        }

        return initials.Length >= prefix.Length
               && initials.ToString().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSubsequence(string label, string prefix)
    {
        var index = 0;
        foreach (var c in label)
        {
            if (index < prefix.Length && char.ToUpperInvariant(c) == char.ToUpperInvariant(prefix[index]))
            {
                index++;
            }
        }

        return index == prefix.Length;
    }
}
