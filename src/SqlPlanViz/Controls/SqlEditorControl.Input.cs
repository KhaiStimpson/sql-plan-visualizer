using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using SqlPlanViz.Editing;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Text.Core;

namespace SqlPlanViz.Controls;

/// <summary>
/// Input for <see cref="SqlEditorControl"/>: keyboard, pointer, clipboard and IME.
///
/// Text arrives through <see cref="CoreTextEditContext"/> where the system will give us one.
/// That is what makes composition input, dead keys and the touch keyboard behave, and the
/// plan calls it out as the detail separating a real editor from a toy. It is not universally
/// available to an unpackaged desktop app, so a <see cref="UIElement.CharacterReceived"/>
/// fallback keeps plain typing working when the edit context cannot be created — degraded
/// (no composition) rather than dead.
/// </summary>
public sealed partial class SqlEditorControl
{
    private const int IndentWidth = 4;

    private static readonly TimeSpan MultiClickWindow = TimeSpan.FromMilliseconds(450);

    private CoreTextEditContext? _editContext;

    /// <summary>Set while applying an edit that came *from* the edit context, to stop the notify loop.</summary>
    private bool _applyingEditContextUpdate;

    private bool _pointerSelecting;
    private DateTime _lastClickUtc;
    private Point _lastClickPoint;
    private int _clickCount;

    /// <summary>
    /// Converts a rect in this control's coordinates to screen coordinates, for IME candidate
    /// window placement. The host sets it; without it the candidate window still appears, just
    /// anchored to the window rather than to the caret.
    /// </summary>
    public Func<Rect, Rect>? ClientToScreen { get; set; }

    private void AttachInput()
    {
        GotFocus += OnGotFocus;
        LostFocus += OnLostFocus;

        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerWheelChanged += OnPointerWheelChanged;

        KeyDown += OnKeyDown;
        CharacterReceived += OnCharacterReceived;

        TryCreateEditContext();
    }

    // ---- Focus -------------------------------------------------------------

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        _hasFocus = true;
        _caretVisible = true;
        _caretTimer.Start();
        _editContext?.NotifyFocusEnter();
        Redraw();
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        _hasFocus = false;
        _caretTimer.Stop();
        _document.EndUndoGroup();
        _editContext?.NotifyFocusLeave();
        DismissCompletion(CompletionDismissReason.FocusLost);
        Redraw();
    }

    // ---- Pointer -----------------------------------------------------------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        var point = e.GetCurrentPoint(_canvas);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var position = point.Position;

        // The gutter is a click target of its own: a mark selects the operator it blames.
        if (position.X < _gutterWidth)
        {
            var line = Math.Clamp(
                (int)((position.Y + _scrollY) / _lineHeight),
                0,
                _document.LineCount - 1);
            var mark = GutterMarks.FirstOrDefault(m => m.Line == line);
            if (mark is not null)
            {
                GutterMarkClicked?.Invoke(this, mark);
                e.Handled = true;
                return;
            }

            // No mark: select the whole line, matching every other editor's gutter.
            MoveCaret(_document.GetLineStart(line), extendSelection: false);
            MoveCaret(
                line + 1 < _document.LineCount ? _document.GetLineStart(line + 1) : _document.Length,
                extendSelection: true);
            e.Handled = true;
            return;
        }

        var now = DateTime.UtcNow;
        var near = Math.Abs(position.X - _lastClickPoint.X) < 4 && Math.Abs(position.Y - _lastClickPoint.Y) < 4;
        _clickCount = now - _lastClickUtc <= MultiClickWindow && near ? _clickCount + 1 : 1;
        _lastClickUtc = now;
        _lastClickPoint = position;

        var offset = OffsetAt(position);
        _document.EndUndoGroup();

        switch (_clickCount)
        {
            case 2:
            {
                var (start, length) = _document.WordAt(offset);
                SelectRange(start, length);
                break;
            }

            case >= 3:
            {
                var line = _document.LineOf(offset);
                var start = _document.GetLineStart(line);
                var end = line + 1 < _document.LineCount ? _document.GetLineStart(line + 1) : _document.Length;
                SelectRange(start, end - start);
                break;
            }

            default:
                MoveCaret(offset, extendSelection: IsDown(VirtualKey.Shift));
                _pointerSelecting = true;
                _canvas.CapturePointer(e.Pointer);
                break;
        }

        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerSelecting)
        {
            return;
        }

        var position = e.GetCurrentPoint(_canvas).Position;

        // Dragging past an edge scrolls, so a selection can run beyond the viewport.
        if (position.Y < 0)
        {
            SetScroll(_scrollX, _scrollY - _lineHeight);
        }
        else if (position.Y > ViewportHeight)
        {
            SetScroll(_scrollX, _scrollY + _lineHeight);
        }

        MoveCaret(OffsetAt(position), extendSelection: true);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerSelecting)
        {
            _pointerSelecting = false;
            _canvas.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(_canvas).Properties.MouseWheelDelta;

        if (IsDown(VirtualKey.Shift))
        {
            SetScroll(_scrollX - (delta / 120.0 * _charWidth * 6), _scrollY);
        }
        else
        {
            SetScroll(_scrollX, _scrollY - (delta / 120.0 * _lineHeight * 3));
        }

        DismissCompletion(CompletionDismissReason.Scrolled);
        e.Handled = true;
    }

    // ---- Keyboard ----------------------------------------------------------

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>
    /// Tab is a focus-navigation key, so it never reaches KeyDown unless it is claimed before
    /// the focus manager gets it.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Tab && !IsReadOnly && _hasFocus)
        {
            if (HandleCompletionKey(e.Key))
            {
                e.Handled = true;
                return;
            }

            IndentSelection(outdent: IsDown(VirtualKey.Shift));
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);

        // The completion list owns the arrows, Enter, Tab and Esc while it is open.
        if (HandleCompletionKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Left:
                MoveCaret(ctrl ? _document.NextWordBoundary(_caret, -1) : _caret - 1, shift);
                break;

            case VirtualKey.Right:
                MoveCaret(ctrl ? _document.NextWordBoundary(_caret, 1) : _caret + 1, shift);
                break;

            case VirtualKey.Up:
                MoveCaretByLine(-1, shift);
                break;

            case VirtualKey.Down:
                MoveCaretByLine(1, shift);
                break;

            case VirtualKey.PageUp:
                MoveCaretByLine(-VisibleLineCount(), shift);
                break;

            case VirtualKey.PageDown:
                MoveCaretByLine(VisibleLineCount(), shift);
                break;

            case VirtualKey.Home:
            {
                if (ctrl)
                {
                    MoveCaret(0, shift);
                    break;
                }

                // Home toggles between the first non-blank column and column 0, which is what
                // makes it useful in indented SQL.
                var line = _document.LineOf(_caret);
                var smart = _document.FirstNonWhitespace(line);
                MoveCaret(_caret == smart ? _document.GetLineStart(line) : smart, shift);
                break;
            }

            case VirtualKey.End:
                MoveCaret(ctrl ? _document.Length : _document.GetLineEnd(_document.LineOf(_caret)), shift);
                break;

            case VirtualKey.Back:
                HandleBackspace(ctrl);
                break;

            case VirtualKey.Delete:
                HandleDelete(ctrl);
                break;

            case VirtualKey.Enter:
                InsertNewLine();
                break;

            case VirtualKey.A when ctrl:
                SelectAll();
                break;

            case VirtualKey.C when ctrl:
                CopySelection();
                break;

            case VirtualKey.X when ctrl:
                CutSelection();
                break;

            case VirtualKey.V when ctrl:
                _ = PasteAsync();
                break;

            case VirtualKey.Z when ctrl && !shift:
                ApplyHistory(_document.Undo());
                break;

            case VirtualKey.Y when ctrl:
            case VirtualKey.Z when ctrl && shift:
                ApplyHistory(_document.Redo());
                break;

            case VirtualKey.Space when ctrl:
                RequestCompletion(explicitInvoke: true);
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        // With an edit context in play, text arrives through TextUpdating instead; taking it
        // here as well would double every keystroke.
        if (_editContext is not null || IsReadOnly)
        {
            return;
        }

        var c = e.Character;
        if (char.IsControl(c) && c is not '\t')
        {
            return;
        }

        TypeText(c.ToString());
        e.Handled = true;
    }

    private int VisibleLineCount() => Math.Max(1, (int)(ViewportHeight / _lineHeight) - 1);

    // ---- Caret and selection ----------------------------------------------

    private void MoveCaret(int offset, bool extendSelection, bool keepDesiredColumn = false)
    {
        var clamped = Math.Clamp(offset, 0, _document.Length);
        var moved = clamped != _caret;

        _caret = clamped;
        if (!extendSelection)
        {
            _anchor = clamped;
        }

        if (!keepDesiredColumn)
        {
            _desiredColumn = -1;
        }

        if (moved)
        {
            _document.EndUndoGroup();
        }

        _document.CaretHint = _caret;
        _caretVisible = true;
        NotifySelectionChangedToEditContext();
        ScrollToCaret();
        CaretMoved?.Invoke(this, EventArgs.Empty);
        OnCaretMovedForCompletion(moved);
        Redraw();
    }

    private void MoveCaretByLine(int delta, bool extendSelection)
    {
        var (line, column) = _document.PositionOf(_caret);
        if (_desiredColumn < 0)
        {
            _desiredColumn = column;
        }

        var targetLine = Math.Clamp(line + delta, 0, _document.LineCount - 1);
        MoveCaret(_document.OffsetOf(targetLine, _desiredColumn), extendSelection, keepDesiredColumn: true);
    }

    public void SelectRange(int start, int length)
    {
        _anchor = Math.Clamp(start, 0, _document.Length);
        _caret = Math.Clamp(start + length, 0, _document.Length);
        _desiredColumn = -1;
        _document.CaretHint = _caret;
        NotifySelectionChangedToEditContext();
        ScrollToCaret();
        CaretMoved?.Invoke(this, EventArgs.Empty);
        Redraw();
    }

    public void SelectAll() => SelectRange(0, _document.Length);

    private void ApplyHistory(int? caret)
    {
        if (caret is int offset)
        {
            _caret = _anchor = Math.Clamp(offset, 0, _document.Length);
            _document.CaretHint = _caret;
            NotifySelectionChangedToEditContext();
            ScrollToCaret();
            CaretMoved?.Invoke(this, EventArgs.Empty);
        }

        DismissCompletion(CompletionDismissReason.DocumentReplaced);
        Redraw();
    }

    // ---- Editing primitives -----------------------------------------------

    /// <summary>Replaces the selection with <paramref name="text"/>. The one path all typing takes.</summary>
    public void TypeText(string text)
    {
        if (IsReadOnly || string.IsNullOrEmpty(text))
        {
            return;
        }

        var start = SelectionStart;
        var length = SelectionLength;
        if (length > 0)
        {
            _document.EndUndoGroup();
        }

        _document.CaretHint = start;
        _document.Replace(start, length, text);
        _caret = _anchor = start + text.Length;
        _desiredColumn = -1;
        _document.CaretHint = _caret;
        NotifySelectionChangedToEditContext();
        ScrollToCaret();
        CaretMoved?.Invoke(this, EventArgs.Empty);
        OnTextTypedForCompletion(text);
    }

    /// <summary>Replaces an explicit range — how a completion commits over the prefix already typed.</summary>
    public void ReplaceRange(int start, int length, string text)
    {
        if (IsReadOnly)
        {
            return;
        }

        _document.EndUndoGroup();
        _document.CaretHint = start;
        _document.Replace(start, length, text);
        _document.EndUndoGroup();
        _caret = _anchor = Math.Clamp(start + text.Length, 0, _document.Length);
        _desiredColumn = -1;
        _document.CaretHint = _caret;
        NotifySelectionChangedToEditContext();
        ScrollToCaret();
        CaretMoved?.Invoke(this, EventArgs.Empty);
    }

    private void HandleBackspace(bool wholeWord)
    {
        if (IsReadOnly)
        {
            return;
        }

        if (HasSelection)
        {
            DeleteSelection();
            OnTextTypedForCompletion(string.Empty);
            return;
        }

        if (_caret == 0)
        {
            return;
        }

        var from = wholeWord ? _document.NextWordBoundary(_caret, -1) : PreviousCaretStop(_caret);
        _document.CaretHint = _caret;
        _document.Replace(from, _caret - from, string.Empty);
        _caret = _anchor = from;
        _document.CaretHint = _caret;
        NotifySelectionChangedToEditContext();
        ScrollToCaret();
        OnTextTypedForCompletion(string.Empty);
    }

    private void HandleDelete(bool wholeWord)
    {
        if (IsReadOnly)
        {
            return;
        }

        if (HasSelection)
        {
            DeleteSelection();
            return;
        }

        if (_caret >= _document.Length)
        {
            return;
        }

        var to = wholeWord ? _document.NextWordBoundary(_caret, 1) : NextCaretStop(_caret);
        _document.CaretHint = _caret;
        _document.Replace(_caret, to - _caret, string.Empty);
        NotifySelectionChangedToEditContext();
        OnTextTypedForCompletion(string.Empty);
    }

    private void DeleteSelection()
    {
        var start = SelectionStart;
        var length = SelectionLength;
        if (length == 0)
        {
            return;
        }

        _document.EndUndoGroup();
        _document.CaretHint = start;
        _document.Replace(start, length, string.Empty);
        _caret = _anchor = start;
        _document.CaretHint = _caret;
        NotifySelectionChangedToEditContext();
        ScrollToCaret();
    }

    /// <summary>Steps over a CRLF pair and over surrogate pairs, so one Backspace deletes one thing.</summary>
    private int PreviousCaretStop(int offset)
    {
        if (offset >= 2 && _document[offset - 2] == '\r' && _document[offset - 1] == '\n')
        {
            return offset - 2;
        }

        if (offset >= 2 && char.IsLowSurrogate(_document[offset - 1]) && char.IsHighSurrogate(_document[offset - 2]))
        {
            return offset - 2;
        }

        return offset - 1;
    }

    private int NextCaretStop(int offset)
    {
        if (offset + 1 < _document.Length && _document[offset] == '\r' && _document[offset + 1] == '\n')
        {
            return offset + 2;
        }

        if (offset + 1 < _document.Length && char.IsHighSurrogate(_document[offset]) && char.IsLowSurrogate(_document[offset + 1]))
        {
            return offset + 2;
        }

        return offset + 1;
    }

    /// <summary>Enter keeps the current line's leading whitespace, which is the whole point in SQL.</summary>
    private void InsertNewLine()
    {
        if (IsReadOnly)
        {
            return;
        }

        var line = _document.LineOf(SelectionStart);
        var lineText = _document.GetLineText(line);
        var indent = new string(lineText.TakeWhile(char.IsWhiteSpace).ToArray());

        _document.EndUndoGroup();
        TypeText("\r\n" + indent);
        _document.EndUndoGroup();
        DismissCompletion(CompletionDismissReason.Committed);
    }

    private void IndentSelection(bool outdent)
    {
        if (IsReadOnly)
        {
            return;
        }

        var start = SelectionStart;
        var end = start + SelectionLength;
        var firstLine = _document.LineOf(start);
        var lastLine = _document.LineOf(Math.Max(start, end - 1));

        // No selection, or one inside a single line: Tab is a literal indent, not a block shift.
        if (!outdent && firstLine == lastLine)
        {
            TypeText(new string(' ', IndentWidth));
            return;
        }

        _document.EndUndoGroup();
        for (var line = lastLine; line >= firstLine; line--)
        {
            var lineStart = _document.GetLineStart(line);
            var text = _document.GetLineText(line);

            if (outdent)
            {
                var strip = 0;
                while (strip < IndentWidth && strip < text.Length && text[strip] is ' ')
                {
                    strip++;
                }

                if (strip == 0 && text.StartsWith('\t'))
                {
                    strip = 1;
                }

                if (strip > 0)
                {
                    _document.Replace(lineStart, strip, string.Empty);
                }
            }
            else if (text.Length > 0 || line == firstLine)
            {
                _document.Insert(lineStart, new string(' ', IndentWidth));
            }
        }

        _document.EndUndoGroup();
        SelectRange(_document.GetLineStart(firstLine), _document.GetLineEnd(lastLine) - _document.GetLineStart(firstLine));
    }

    // ---- Clipboard ---------------------------------------------------------

    /// <summary>Plain text only, as the plan specifies — RTF from an editor would arrive styled.</summary>
    private void CopySelection()
    {
        if (!HasSelection)
        {
            return;
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(SelectedText);
        Clipboard.SetContent(package);
    }

    private void CutSelection()
    {
        if (!HasSelection || IsReadOnly)
        {
            return;
        }

        CopySelection();
        DeleteSelection();
    }

    private async Task PasteAsync()
    {
        if (IsReadOnly)
        {
            return;
        }

        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                return;
            }

            var text = await content.GetTextAsync();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            _document.EndUndoGroup();
            TypeText(text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n"));
            _document.EndUndoGroup();
        }
        catch (Exception)
        {
            // Another app can hold the clipboard open; a failed paste is not worth an error bar.
        }
    }

    // ---- CoreTextEditContext ----------------------------------------------

    private void TryCreateEditContext()
    {
        try
        {
            var manager = CoreTextServicesManager.GetForCurrentView();
            _editContext = manager.CreateEditContext();
            _editContext.InputPaneDisplayPolicy = CoreTextInputPaneDisplayPolicy.Automatic;
            _editContext.InputScope = CoreTextInputScope.Text;

            _editContext.TextRequested += OnTextRequested;
            _editContext.SelectionRequested += OnSelectionRequested;
            _editContext.TextUpdating += OnTextUpdating;
            _editContext.SelectionUpdating += OnSelectionUpdating;
            _editContext.FormatUpdating += (_, args) => args.Result = CoreTextFormatUpdatingResult.Succeeded;
            _editContext.LayoutRequested += OnLayoutRequested;
            _editContext.FocusRemoved += (_, _) => _hasFocus = false;
        }
        catch (Exception)
        {
            // No edit context available in this host: fall back to CharacterReceived. Typing
            // still works; composition-based input does not.
            _editContext = null;
        }
    }

    private void OnTextRequested(CoreTextEditContext sender, CoreTextTextRequestedEventArgs args)
    {
        var request = args.Request;
        var start = Math.Clamp(request.Range.StartCaretPosition, 0, _document.Length);
        var end = Math.Clamp(request.Range.EndCaretPosition, start, _document.Length);
        request.Text = _document.GetText(start, end - start);
        request.Range = new CoreTextRange { StartCaretPosition = start, EndCaretPosition = end };
    }

    private void OnSelectionRequested(CoreTextEditContext sender, CoreTextSelectionRequestedEventArgs args) =>
        args.Request.Selection = new CoreTextRange
        {
            StartCaretPosition = SelectionStart,
            EndCaretPosition = SelectionStart + SelectionLength,
        };

    private void OnTextUpdating(CoreTextEditContext sender, CoreTextTextUpdatingEventArgs args)
    {
        if (IsReadOnly)
        {
            args.Result = CoreTextTextUpdatingResult.Failed;
            return;
        }

        var start = Math.Clamp(args.Range.StartCaretPosition, 0, _document.Length);
        var end = Math.Clamp(args.Range.EndCaretPosition, start, _document.Length);

        _applyingEditContextUpdate = true;
        try
        {
            _document.CaretHint = start;
            _document.Replace(start, end - start, args.Text ?? string.Empty);
            _caret = Math.Clamp(args.NewSelection.EndCaretPosition, 0, _document.Length);
            _anchor = Math.Clamp(args.NewSelection.StartCaretPosition, 0, _document.Length);
            _document.CaretHint = _caret;
        }
        finally
        {
            _applyingEditContextUpdate = false;
        }

        args.Result = CoreTextTextUpdatingResult.Succeeded;
        _desiredColumn = -1;
        ScrollToCaret();
        CaretMoved?.Invoke(this, EventArgs.Empty);
        OnTextTypedForCompletion(args.Text ?? string.Empty);
        Redraw();
    }

    private void OnSelectionUpdating(CoreTextEditContext sender, CoreTextSelectionUpdatingEventArgs args)
    {
        _anchor = Math.Clamp(args.Selection.StartCaretPosition, 0, _document.Length);
        _caret = Math.Clamp(args.Selection.EndCaretPosition, 0, _document.Length);
        _document.CaretHint = _caret;
        args.Result = CoreTextSelectionUpdatingResult.Succeeded;
        Redraw();
    }

    private void OnLayoutRequested(CoreTextEditContext sender, CoreTextLayoutRequestedEventArgs args)
    {
        var caret = CaretRect();
        var control = new Rect(0, 0, ActualWidth, ActualHeight);

        args.Request.LayoutBounds.TextBounds = ClientToScreen?.Invoke(caret) ?? caret;
        args.Request.LayoutBounds.ControlBounds = ClientToScreen?.Invoke(control) ?? control;
    }

    private void NotifyTextChangedToEditContext(DocumentChangedEventArgs e)
    {
        if (_editContext is null || _applyingEditContextUpdate)
        {
            return;
        }

        var modified = new CoreTextRange
        {
            StartCaretPosition = e.Offset,
            EndCaretPosition = e.Offset + e.RemovedLength,
        };

        var selection = new CoreTextRange
        {
            StartCaretPosition = Math.Clamp(SelectionStart, 0, _document.Length),
            EndCaretPosition = Math.Clamp(SelectionStart + SelectionLength, 0, _document.Length),
        };

        _editContext.NotifyTextChanged(modified, e.InsertedText.Length, selection);
    }

    private void NotifySelectionChangedToEditContext()
    {
        if (_editContext is null || _applyingEditContextUpdate)
        {
            return;
        }

        _editContext.NotifySelectionChanged(new CoreTextRange
        {
            StartCaretPosition = Math.Clamp(SelectionStart, 0, _document.Length),
            EndCaretPosition = Math.Clamp(SelectionStart + SelectionLength, 0, _document.Length),
        });
    }

    private void NotifyLayoutChangedToEditContext() => _editContext?.NotifyLayoutChanged();
}
