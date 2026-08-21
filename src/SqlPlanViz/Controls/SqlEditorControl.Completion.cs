namespace SqlPlanViz.Controls;

/// <summary>Why a completion list closed. Kept apart from the key handling so the reasons stay readable.</summary>
public enum CompletionDismissReason
{
    Escaped,
    Committed,
    CaretMoved,
    FocusLost,
    Scrolled,
    DocumentReplaced,
}

/// <summary>
/// Completion hooks on the editor. Phase 1 ships them inert: the editor is delivered
/// standalone and judged before anything depends on it, and these are the seams the Phase 2
/// completion engine plugs into rather than reaching into the input handling.
/// </summary>
public sealed partial class SqlEditorControl
{
    /// <summary>True while a completion list is showing, so the editor yields the arrows to it.</summary>
    public bool IsCompletionOpen => false;

    private void RequestCompletion(bool explicitInvoke)
    {
    }

    private bool HandleCompletionKey(Windows.System.VirtualKey key) => false;

    private void DismissCompletion(CompletionDismissReason reason)
    {
    }

    private void OnTextTypedForCompletion(string text)
    {
    }

    private void OnCaretMovedForCompletion(bool moved)
    {
    }
}
