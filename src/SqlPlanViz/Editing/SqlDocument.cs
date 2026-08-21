namespace SqlPlanViz.Editing;

/// <summary>A single primitive edit: replace <paramref name="RemovedText"/> at <paramref name="Offset"/> with <paramref name="InsertedText"/>.</summary>
public sealed record TextChange(int Offset, string RemovedText, string InsertedText)
{
    public TextChange Inverted => new(Offset, InsertedText, RemovedText);
}

public sealed class DocumentChangedEventArgs : EventArgs
{
    /// <summary>Character offset the change started at.</summary>
    public int Offset { get; init; }

    public int RemovedLength { get; init; }

    public string InsertedText { get; init; } = string.Empty;

    /// <summary>First line index touched. Valid against both the old and the new line list.</summary>
    public int StartLine { get; init; }

    /// <summary>Last line touched, indexed into the line list <em>before</em> the change.</summary>
    public int EndLineBefore { get; init; }

    /// <summary>Last line touched, indexed into the line list <em>after</em> the change.</summary>
    public int EndLineAfter { get; init; }

    /// <summary>Set when the whole buffer was replaced, so consumers skip incremental work.</summary>
    public bool IsWholeDocument { get; init; }
}

/// <summary>
/// The editor's text buffer (live-plan-editor-plan.md Phase 1).
///
/// Deliberately free of any WinUI or Win2D dependency: the tokenizer, the completion engine,
/// the parameter extractor and the batch composer all consume this type, and none of them
/// should need a UI thread to be tested. The control in <c>Controls/SqlEditorControl.cs</c>
/// is the only thing that renders it.
///
/// A <see cref="System.Text.StringBuilder"/> backs the text and a line-start index is
/// maintained incrementally, so a keystroke costs O(edited line) rather than O(document).
/// </summary>
public sealed class SqlDocument
{
    /// <summary>Consecutive typing merges into one undo unit while it stays within this window.</summary>
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(900);

    private readonly System.Text.StringBuilder _buffer = new();

    /// <summary>Character offset each line starts at. Always contains at least one entry (0).</summary>
    private readonly List<int> _lineStarts = [0];

    private readonly List<UndoUnit> _undo = [];
    private readonly List<UndoUnit> _redo = [];

    private string? _cachedText;
    private UndoUnit? _openUnit;
    private DateTime _openUnitTouchedUtc;
    private bool _suppressUndo;

    public SqlDocument()
    {
    }

    public SqlDocument(string text) => SetText(text);

    public event EventHandler<DocumentChangedEventArgs>? Changed;

    /// <summary>Raised when the undo or redo stack becomes empty or non-empty.</summary>
    public event EventHandler? HistoryChanged;

    public string Text => _cachedText ??= _buffer.ToString();

    public int Length => _buffer.Length;

    public int LineCount => _lineStarts.Count;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// Incremented on every mutation. Consumers (the tokenizer cache, the completion engine)
    /// compare it instead of holding on to the text.
    /// </summary>
    public int Version { get; private set; }

    public char this[int offset] => _buffer[offset];

    public int GetLineStart(int line) => _lineStarts[Math.Clamp(line, 0, _lineStarts.Count - 1)];

    /// <summary>Exclusive end of the line, not counting its newline.</summary>
    public int GetLineEnd(int line)
    {
        line = Math.Clamp(line, 0, _lineStarts.Count - 1);
        var end = line + 1 < _lineStarts.Count ? _lineStarts[line + 1] : _buffer.Length;

        // Trim the newline the next line's start sits after.
        while (end > _lineStarts[line] && (_buffer[end - 1] == '\n' || _buffer[end - 1] == '\r'))
        {
            end--;
        }

        return end;
    }

    public int GetLineLength(int line) => GetLineEnd(line) - GetLineStart(line);

    public string GetLineText(int line)
    {
        var start = GetLineStart(line);
        return _buffer.ToString(start, GetLineEnd(line) - start);
    }

    public string GetText(int offset, int length)
    {
        offset = Math.Clamp(offset, 0, _buffer.Length);
        length = Math.Clamp(length, 0, _buffer.Length - offset);
        return _buffer.ToString(offset, length);
    }

    /// <summary>
    /// Every non-overlapping occurrence of <paramref name="query"/>, left to right (the find
    /// overlay's own helper — SqlPlanViz.Controls.SqlEditorControl.Find.cs draws the results).
    /// </summary>
    public IReadOnlyList<int> FindAll(string query, bool matchCase = false)
    {
        if (string.IsNullOrEmpty(query))
        {
            return [];
        }

        var text = Text;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var results = new List<int>();
        var from = 0;
        while (from <= text.Length - query.Length)
        {
            var index = text.IndexOf(query, from, comparison);
            if (index < 0)
            {
                break;
            }

            results.Add(index);
            from = index + query.Length;
        }

        return results;
    }

    /// <summary>Zero-based line index containing <paramref name="offset"/>.</summary>
    public int LineOf(int offset)
    {
        offset = Math.Clamp(offset, 0, _buffer.Length);
        var index = _lineStarts.BinarySearch(offset);
        return index >= 0 ? index : ~index - 1;
    }

    public (int Line, int Column) PositionOf(int offset)
    {
        var line = LineOf(offset);
        return (line, Math.Clamp(offset, 0, _buffer.Length) - _lineStarts[line]);
    }

    public int OffsetOf(int line, int column)
    {
        line = Math.Clamp(line, 0, _lineStarts.Count - 1);
        return Math.Clamp(_lineStarts[line] + Math.Max(0, column), _lineStarts[line], GetLineEnd(line));
    }

    /// <summary>Replaces the entire buffer and clears the undo history — used on load and re-plan.</summary>
    public void SetText(string text)
    {
        text ??= string.Empty;
        _buffer.Clear();
        _buffer.Append(text);
        _cachedText = text;
        RebuildLineIndex(0);
        _undo.Clear();
        _redo.Clear();
        _openUnit = null;
        Version++;

        Changed?.Invoke(this, new DocumentChangedEventArgs
        {
            Offset = 0,
            RemovedLength = 0,
            InsertedText = text,
            StartLine = 0,
            EndLineBefore = 0,
            EndLineAfter = Math.Max(0, _lineStarts.Count - 1),
            IsWholeDocument = true,
        });
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Insert(int offset, string text) => Replace(offset, 0, text);

    public void Remove(int offset, int length) => Replace(offset, length, string.Empty);

    /// <summary>The one mutation primitive. Everything else routes through here.</summary>
    public void Replace(int offset, int length, string text)
    {
        text ??= string.Empty;
        offset = Math.Clamp(offset, 0, _buffer.Length);
        length = Math.Clamp(length, 0, _buffer.Length - offset);
        if (length == 0 && text.Length == 0)
        {
            return;
        }

        var removed = _buffer.ToString(offset, length);
        if (!_suppressUndo)
        {
            RecordUndo(new TextChange(offset, removed, text));
        }

        ApplyCore(offset, length, removed, text);
    }

    private void ApplyCore(int offset, int length, string removed, string inserted)
    {
        var startLine = LineOf(offset);
        var endLineBefore = LineOf(offset + length);

        _buffer.Remove(offset, length);
        _buffer.Insert(offset, inserted);
        _cachedText = null;
        Version++;

        RebuildLineIndex(startLine);
        var endLineAfter = LineOf(offset + inserted.Length);

        Changed?.Invoke(this, new DocumentChangedEventArgs
        {
            Offset = offset,
            RemovedLength = length,
            InsertedText = inserted,
            StartLine = startLine,
            EndLineBefore = endLineBefore,
            EndLineAfter = endLineAfter,
        });
    }

    /// <summary>
    /// Recomputes line starts from <paramref name="fromLine"/> onwards. Everything before the
    /// edited line is untouched by definition, so a keystroke on line 400 does not re-scan
    /// lines 1-399 — but the tail after the edit does have to be rescanned, since an inserted
    /// newline shifts every subsequent line start.
    /// </summary>
    private void RebuildLineIndex(int fromLine)
    {
        fromLine = Math.Clamp(fromLine, 0, Math.Max(0, _lineStarts.Count - 1));
        var scanFrom = _lineStarts[fromLine];
        _lineStarts.RemoveRange(fromLine + 1, _lineStarts.Count - fromLine - 1);

        for (var i = scanFrom; i < _buffer.Length; i++)
        {
            var c = _buffer[i];
            if (c == '\r')
            {
                // Treat CRLF as one break; a lone CR is a break too.
                if (i + 1 < _buffer.Length && _buffer[i + 1] == '\n')
                {
                    i++;
                }

                _lineStarts.Add(i + 1);
            }
            else if (c == '\n')
            {
                _lineStarts.Add(i + 1);
            }
        }
    }

    // ---- Undo / redo -------------------------------------------------------

    private sealed class UndoUnit
    {
        public List<TextChange> Changes { get; } = [];

        public int CaretBefore { get; set; }

        public int CaretAfter { get; set; }

        public UndoKind Kind { get; set; }
    }

    private enum UndoKind
    {
        Other,
        Typing,
        Deleting,
    }

    /// <summary>
    /// Caret offset the editor will restore to when this unit is undone. The control keeps
    /// this current so undo puts the caret where the typing started, not at offset 0.
    /// </summary>
    public int CaretHint { get; set; }

    /// <summary>
    /// Closes the current coalescing unit. The control calls this on caret jumps, focus loss,
    /// paste, and before any programmatic edit, so those never merge into the user's typing.
    /// </summary>
    public void EndUndoGroup()
    {
        _openUnit = null;
    }

    private void RecordUndo(TextChange change)
    {
        _redo.Clear();

        var kind = Classify(change);
        var now = DateTime.UtcNow;

        if (_openUnit is { } open
            && open.Kind == kind
            && kind != UndoKind.Other
            && now - _openUnitTouchedUtc <= CoalesceWindow
            && CanMerge(open, change, kind))
        {
            open.Changes.Add(change);
            open.CaretAfter = change.Offset + change.InsertedText.Length;
            _openUnitTouchedUtc = now;
            return;
        }

        var unit = new UndoUnit
        {
            Kind = kind,
            CaretBefore = CaretHint,
            CaretAfter = change.Offset + change.InsertedText.Length,
        };
        unit.Changes.Add(change);
        _undo.Add(unit);
        _openUnit = kind == UndoKind.Other ? null : unit;
        _openUnitTouchedUtc = now;
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private static UndoKind Classify(TextChange change)
    {
        // Newlines end a unit: undoing a paragraph of typing in one stroke is surprising.
        if (change.RemovedText.Length == 0
            && change.InsertedText.Length == 1
            && change.InsertedText[0] is not ('\n' or '\r'))
        {
            return UndoKind.Typing;
        }

        if (change.InsertedText.Length == 0 && change.RemovedText.Length == 1)
        {
            return UndoKind.Deleting;
        }

        return UndoKind.Other;
    }

    private static bool CanMerge(UndoUnit unit, TextChange change, UndoKind kind)
    {
        var last = unit.Changes[^1];
        if (kind == UndoKind.Typing)
        {
            // Contiguous, and a word boundary starts a fresh unit so undo steps read as words.
            if (last.Offset + last.InsertedText.Length != change.Offset)
            {
                return false;
            }

            var previous = last.InsertedText[^1];
            var current = change.InsertedText[0];
            return IsWordChar(previous) == IsWordChar(current);
        }

        // Backspace runs leftwards; Delete runs in place.
        return last.Offset == change.Offset + change.RemovedText.Length || last.Offset == change.Offset;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';

    /// <summary>Undoes one unit and returns the caret offset to restore, or null when there was nothing to undo.</summary>
    public int? Undo()
    {
        if (_undo.Count == 0)
        {
            return null;
        }

        var unit = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _openUnit = null;

        _suppressUndo = true;
        try
        {
            for (var i = unit.Changes.Count - 1; i >= 0; i--)
            {
                var inverse = unit.Changes[i].Inverted;
                ApplyCore(inverse.Offset, inverse.RemovedText.Length, inverse.RemovedText, inverse.InsertedText);
            }
        }
        finally
        {
            _suppressUndo = false;
        }

        _redo.Add(unit);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return Math.Clamp(unit.CaretBefore, 0, _buffer.Length);
    }

    public int? Redo()
    {
        if (_redo.Count == 0)
        {
            return null;
        }

        var unit = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _openUnit = null;

        _suppressUndo = true;
        try
        {
            foreach (var change in unit.Changes)
            {
                ApplyCore(change.Offset, change.RemovedText.Length, change.RemovedText, change.InsertedText);
            }
        }
        finally
        {
            _suppressUndo = false;
        }

        _undo.Add(unit);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return Math.Clamp(unit.CaretAfter, 0, _buffer.Length);
    }

    // ---- Word / navigation helpers ----------------------------------------

    /// <summary>Start and length of the word containing (or immediately before) <paramref name="offset"/>.</summary>
    public (int Start, int Length) WordAt(int offset)
    {
        offset = Math.Clamp(offset, 0, _buffer.Length);
        var start = offset;
        while (start > 0 && IsWordChar(_buffer[start - 1]))
        {
            start--;
        }

        var end = offset;
        while (end < _buffer.Length && IsWordChar(_buffer[end]))
        {
            end++;
        }

        return (start, end - start);
    }

    /// <summary>Offset of the next word boundary in <paramref name="direction"/> (+1 or -1).</summary>
    public int NextWordBoundary(int offset, int direction)
    {
        offset = Math.Clamp(offset, 0, _buffer.Length);
        if (direction < 0)
        {
            if (offset == 0)
            {
                return 0;
            }

            var i = offset - 1;
            while (i > 0 && char.IsWhiteSpace(_buffer[i]) && _buffer[i] is not ('\n' or '\r'))
            {
                i--;
            }

            if (IsWordChar(_buffer[i]))
            {
                while (i > 0 && IsWordChar(_buffer[i - 1]))
                {
                    i--;
                }
            }

            return i;
        }

        if (offset >= _buffer.Length)
        {
            return _buffer.Length;
        }

        var j = offset;
        if (IsWordChar(_buffer[j]))
        {
            while (j < _buffer.Length && IsWordChar(_buffer[j]))
            {
                j++;
            }
        }
        else
        {
            j++;
        }

        while (j < _buffer.Length && char.IsWhiteSpace(_buffer[j]) && _buffer[j] is not ('\n' or '\r'))
        {
            j++;
        }

        return j;
    }

    /// <summary>First non-whitespace offset on the line — where Home lands before column 0.</summary>
    public int FirstNonWhitespace(int line)
    {
        var start = GetLineStart(line);
        var end = GetLineEnd(line);
        var i = start;
        while (i < end && char.IsWhiteSpace(_buffer[i]))
        {
            i++;
        }

        return i == end ? start : i;
    }
}
