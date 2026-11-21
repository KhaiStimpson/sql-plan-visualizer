using Microsoft.Graphics.Canvas;
using SqlPlanViz.Editing;

namespace SqlPlanViz.Controls;

/// <summary>
/// Bracket/paren match highlighting (docs/editor-and-parameters-ux-plan.md Phase 5). Rides on
/// the existing <see cref="TSqlTokenizer"/> token stream that already powers syntax colouring —
/// a bracket only counts if the tokenizer classified it as <see cref="SqlTokenClass.Punctuation"/>,
/// so one sitting inside a string or comment is correctly ignored without any new parsing.
/// </summary>
public sealed partial class SqlEditorControl
{
    private static readonly Dictionary<char, char> OpenToClose = new() { ['('] = ')', ['['] = ']', ['{'] = '}' };

    private static readonly Dictionary<char, char> CloseToOpen = new() { [')'] = '(', [']'] = '[', ['}'] = '{' };

    /// <summary>Recomputes <see cref="_bracketMatch"/> for the caret's current position.</summary>
    private void UpdateBracketMatch()
    {
        var match = FindBracketMatch();
        if (match == _bracketMatch)
        {
            return;
        }

        _bracketMatch = match;
        Redraw();
    }

    private (int Open, int Close)? FindBracketMatch()
    {
        if (HasSelection)
        {
            return null;
        }

        // A bracket immediately before the caret counts too, so the pair lights up the moment
        // you land just past the closing character, not only just before it.
        if (TryMatchAt(_caret, out var match))
        {
            return match;
        }

        return _caret > 0 && TryMatchAt(_caret - 1, out match) ? match : null;
    }

    private bool TryMatchAt(int offset, out (int Open, int Close) match)
    {
        match = default;
        if (offset < 0 || offset >= _document.Length || _tokenizer.ClassAt(offset) != SqlTokenClass.Punctuation)
        {
            return false;
        }

        var ch = _document[offset];
        if (OpenToClose.TryGetValue(ch, out var closer))
        {
            if (ScanForBracket(offset + 1, +1, ch, closer) is int end)
            {
                match = (offset, end);
                return true;
            }
        }
        else if (CloseToOpen.TryGetValue(ch, out var opener))
        {
            if (ScanForBracket(offset - 1, -1, opener, ch) is int start)
            {
                match = (start, offset);
                return true;
            }
        }

        return false;
    }

    /// <summary>Walks token-classified punctuation from <paramref name="from"/>, tracking nesting depth.</summary>
    private int? ScanForBracket(int from, int direction, char open, char close)
    {
        var depth = 0;
        for (var i = from; i >= 0 && i < _document.Length; i += direction)
        {
            if (_tokenizer.ClassAt(i) != SqlTokenClass.Punctuation)
            {
                continue;
            }

            var ch = _document[i];
            var opens = direction > 0 ? ch == open : ch == close;
            var closes = direction > 0 ? ch == close : ch == open;

            if (opens)
            {
                depth++;
            }
            else if (closes)
            {
                if (depth == 0)
                {
                    return i;
                }

                depth--;
            }
        }

        return null;
    }

    private void DrawBracketMatch(CanvasDrawingSession ds, int firstVisibleLine, int lastVisibleLine)
    {
        if (_bracketMatch is not { } match)
        {
            return;
        }

        DrawBracketHighlight(ds, match.Open, firstVisibleLine, lastVisibleLine);
        DrawBracketHighlight(ds, match.Close, firstVisibleLine, lastVisibleLine);
    }

    private void DrawBracketHighlight(CanvasDrawingSession ds, int offset, int firstVisibleLine, int lastVisibleLine)
    {
        var line = _document.LineOf(offset);
        if (line < firstVisibleLine || line > lastVisibleLine)
        {
            return;
        }

        var column = offset - _document.GetLineStart(line);
        var x0 = ColumnXExact(line, column);
        var x1 = ColumnXExact(line, column + 1);
        ds.FillRectangle(x0, LineTop(line), Math.Max(2f, x1 - x0), _lineHeight, _theme.BracketMatchFill);
    }
}
