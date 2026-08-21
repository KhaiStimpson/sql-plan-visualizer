using Microsoft.UI.Xaml;
using SqlPlanViz.Editing;
using SqlPlanViz.Editing.Completion;
using Windows.Foundation;
using Windows.System;

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
    NoMatches,
}

/// <summary>
/// Completion behaviour for <see cref="SqlEditorControl"/> (live-plan-editor-plan.md Phase 2).
///
/// The editor owns the keyboard model and the popup's lifetime; the engine owns what is in
/// the list. Nothing here knows what a provider is, which is what lets Phase 6 add the
/// catalog and tuning providers without touching this file.
/// </summary>
public sealed partial class SqlEditorControl
{
    private readonly CompletionPopup _completionPopup = new();

    private CompletionContext? _completionContext;

    /// <summary>Offset the current session's prefix starts at; the caret leaving it dismisses.</summary>
    private int _completionAnchor = -1;

    /// <summary>The engine to query. Null leaves the editor with no completion at all.</summary>
    public CompletionEngine? CompletionEngine { get; set; }

    /// <summary>Type-ahead. Ctrl+Space still works when this is off.</summary>
    public bool AutoCompletionEnabled { get; set; } = true;

    /// <summary>Characters typed before type-ahead fires, so "a" does not open a 60-item list.</summary>
    public int AutoCompletionMinimumPrefix { get; set; } = 2;

    /// <summary>Parser dialect for completion context. Follows the connected server when set.</summary>
    public SqlParserVersion? ParserVersion
    {
        get => _tokenizer.Version;
        set => _tokenizer.Version = value;
    }

    public bool IsCompletionOpen => _completionPopup.IsOpen;

    private void AttachCompletion() =>
        _completionPopup.Committed += (_, item) => CommitCompletion(item);

    private void RequestCompletion(bool explicitInvoke)
    {
        if (CompletionEngine is null || IsReadOnly || XamlRoot is null)
        {
            return;
        }

        var context = CompletionContext.Create(
            _document,
            _caret,
            _tokenizer,
            explicitInvoke,
            ParserVersion);

        var items = CompletionEngine.GetCompletions(context);
        if (items.Count == 0)
        {
            DismissCompletion(CompletionDismissReason.NoMatches);
            return;
        }

        _completionContext = context;
        _completionAnchor = context.ReplaceStart;
        _completionPopup.Show(XamlRoot, items, CaretRectInRoot());
        Redraw();
    }

    /// <summary>The caret rectangle in the XamlRoot's coordinates, which is what a Popup offsets from.</summary>
    private Rect CaretRectInRoot()
    {
        var caret = CaretRect();
        var transform = _canvas.TransformToVisual(null);
        var topLeft = transform.TransformPoint(new Point(caret.X, caret.Y));
        return new Rect(topLeft.X, topLeft.Y, caret.Width, caret.Height);
    }

    /// <summary>
    /// Keys the open list owns. Returns true when the key was consumed, so the editor's own
    /// handler leaves it alone — Enter must insert a newline when nothing is open and accept
    /// an item when something is.
    /// </summary>
    private bool HandleCompletionKey(VirtualKey key)
    {
        if (!_completionPopup.IsOpen)
        {
            return false;
        }

        switch (key)
        {
            case VirtualKey.Up:
                _completionPopup.MoveSelection(-1);
                return true;

            case VirtualKey.Down:
                _completionPopup.MoveSelection(1);
                return true;

            case VirtualKey.PageUp:
                _completionPopup.MoveSelection(-8);
                return true;

            case VirtualKey.PageDown:
                _completionPopup.MoveSelection(8);
                return true;

            case VirtualKey.Escape:
                DismissCompletion(CompletionDismissReason.Escaped);
                return true;

            case VirtualKey.Tab:
            case VirtualKey.Enter:
                if (_completionPopup.SelectedItem is { } selected)
                {
                    CommitCompletion(selected);
                    return true;
                }

                DismissCompletion(CompletionDismissReason.Escaped);
                return false;

            // Anything that moves the caret off the word being completed closes the list, and
            // Left/Right are the cheapest way to do that by accident.
            case VirtualKey.Left:
            case VirtualKey.Right:
            case VirtualKey.Home:
            case VirtualKey.End:
                DismissCompletion(CompletionDismissReason.CaretMoved);
                return false;

            default:
                return false;
        }
    }

    private void CommitCompletion(CompletionItem item)
    {
        if (_completionContext is not { } context)
        {
            DismissCompletion(CompletionDismissReason.Committed);
            return;
        }

        // The replace range came from the context; the user may have typed more since, so
        // extend it to whatever the prefix has grown into rather than duplicating characters.
        // An item may override the range outright — a suggestion that rewrites a predicate or
        // expands a star is not replacing a word.
        var start = item.ReplaceStartOverride ?? context.ReplaceStart;
        var length = item.ReplaceLengthOverride
                     ?? Math.Max(0, Math.Min(_caret, _document.Length) - start);

        start = Math.Clamp(start, 0, _document.Length);
        length = Math.Clamp(length, 0, _document.Length - start);

        DismissCompletion(CompletionDismissReason.Committed);
        ReplaceRange(start, length, item.InsertText);
    }

    private void DismissCompletion(CompletionDismissReason reason)
    {
        if (!_completionPopup.IsOpen && _completionContext is null)
        {
            return;
        }

        _completionPopup.Hide();
        _completionContext = null;
        _completionAnchor = -1;
        Redraw();
    }

    private void OnTextTypedForCompletion(string text)
    {
        if (CompletionEngine is null || IsReadOnly)
        {
            return;
        }

        if (_completionPopup.IsOpen)
        {
            // Re-query rather than re-filter: the clause, the scope and the qualifier can all
            // have changed with the character just typed, and a stale candidate set would
            // keep offering the outer query's columns inside a subquery.
            RequestCompletion(explicitInvoke: _completionContext?.ExplicitlyInvoked ?? false);
            return;
        }

        if (!AutoCompletionEnabled)
        {
            return;
        }

        // A dot always opens the list: it is an explicit request for "what is on this thing".
        if (text.EndsWith('.'))
        {
            RequestCompletion(explicitInvoke: true);
            return;
        }

        if (text.Length != 1 || (!char.IsLetter(text[0]) && text[0] is not ('@' or '_')))
        {
            return;
        }

        var start = PrefixStart();
        var isVariable = start < _document.Length && _document[start] == '@';
        if (_caret - start >= AutoCompletionMinimumPrefix || isVariable)
        {
            RequestCompletion(explicitInvoke: false);
        }
    }

    private int PrefixStart()
    {
        var start = _caret;
        while (start > 0 && (char.IsLetterOrDigit(_document[start - 1]) || _document[start - 1] is '_' or '$'))
        {
            start--;
        }

        if (start > 0 && _document[start - 1] is '@' or '#')
        {
            start--;
        }

        return start;
    }

    private void OnCaretMovedForCompletion(bool moved)
    {
        if (!moved || !_completionPopup.IsOpen)
        {
            return;
        }

        // Still inside the word the session started on? Then this was a keystroke, not a jump.
        if (_completionAnchor >= 0 && _caret >= _completionAnchor && _caret <= _document.Length)
        {
            var word = _document.GetText(_completionAnchor, _caret - _completionAnchor);
            if (!word.Any(c => char.IsWhiteSpace(c)))
            {
                return;
            }
        }

        DismissCompletion(CompletionDismissReason.CaretMoved);
    }
}
