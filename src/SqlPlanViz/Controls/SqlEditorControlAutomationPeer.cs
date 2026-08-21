using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Text;
using SqlPlanViz.Editing;
using Windows.Foundation;

namespace SqlPlanViz.Controls;

/// <summary>
/// Exposes <see cref="SqlEditorControl"/> to assistive technology as a real text control
/// (live-plan-editor-plan.md Phase 1).
///
/// A Win2D surface is, to UI Automation, a blank rectangle: without this the editor would be
/// invisible to a screen reader no matter how well it renders. Both patterns are supported —
/// Text for navigation and reading, Value for the plain "what is in this box" case.
/// </summary>
public sealed class SqlEditorAutomationPeer : FrameworkElementAutomationPeer, ITextProvider, IValueProvider
{
    private readonly SqlEditorControl _owner;

    public SqlEditorAutomationPeer(SqlEditorControl owner)
        : base(owner) => _owner = owner;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;

    protected override string GetClassNameCore() => nameof(SqlEditorControl);

    protected override string GetNameCore()
    {
        var name = base.GetNameCore();
        return string.IsNullOrEmpty(name) ? "T-SQL editor" : name;
    }

    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface is PatternInterface.Text or PatternInterface.Value
            ? this
            : base.GetPatternCore(patternInterface);

    // ---- ITextProvider -----------------------------------------------------

    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;

    public ITextRangeProvider DocumentRange => new SqlEditorTextRange(this, _owner, 0, _owner.Document.Length);

    public ITextRangeProvider[] GetSelection() =>
    [
        new SqlEditorTextRange(this, _owner, _owner.SelectionStart, _owner.SelectionStart + _owner.SelectionLength),
    ];

    public ITextRangeProvider[] GetVisibleRanges() => [DocumentRange];

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) => DocumentRange;

    public ITextRangeProvider RangeFromPoint(Point screenLocation) =>
        new SqlEditorTextRange(this, _owner, _owner.CaretOffset, _owner.CaretOffset);

    // ---- IValueProvider ----------------------------------------------------

    public bool IsReadOnly => _owner.IsReadOnly;

    public string Value => _owner.Text;

    public void SetValue(string value) => _owner.Text = value;

    internal void RaiseTextChanged() =>
        RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged);

    internal void RaiseSelectionChanged() =>
        RaiseAutomationEvent(AutomationEvents.TextPatternOnTextSelectionChanged);

    // ProviderFromPeer is protected on AutomationPeer, so it can only be reached from inside a
    // derived peer's own class body; this wrapper lets SqlEditorTextRange (a sibling class) get at it.
    internal IRawElementProviderSimple GetRawElementProviderSimple() => ProviderFromPeer(this);
}

/// <summary>A span of the editor's text, in document offsets. Endpoints are always ordered.</summary>
internal sealed class SqlEditorTextRange : ITextRangeProvider
{
    private readonly SqlEditorAutomationPeer _peer;
    private readonly SqlEditorControl _owner;
    private int _start;
    private int _end;

    public SqlEditorTextRange(SqlEditorAutomationPeer peer, SqlEditorControl owner, int start, int end)
    {
        _peer = peer;
        _owner = owner;
        _start = Math.Min(start, end);
        _end = Math.Max(start, end);
        Clamp();
    }

    private SqlDocument Document => _owner.Document;

    private void Clamp()
    {
        var length = Document.Length;
        _start = Math.Clamp(_start, 0, length);
        _end = Math.Clamp(_end, _start, length);
    }

    public ITextRangeProvider Clone() => new SqlEditorTextRange(_peer, _owner, _start, _end);

    public bool Compare(ITextRangeProvider range) =>
        range is SqlEditorTextRange other && other._start == _start && other._end == _end;

    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        if (targetRange is not SqlEditorTextRange other)
        {
            return 0;
        }

        var mine = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        var theirs = targetEndpoint == TextPatternRangeEndpoint.Start ? other._start : other._end;
        return mine.CompareTo(theirs);
    }

    public void ExpandToEnclosingUnit(TextUnit unit)
    {
        Clamp();
        switch (unit)
        {
            case TextUnit.Character:
                _end = Math.Min(Document.Length, _start + 1);
                break;

            case TextUnit.Word:
            {
                var (start, length) = Document.WordAt(_start);
                _start = start;
                _end = start + length;
                break;
            }

            case TextUnit.Line:
            case TextUnit.Paragraph:
            {
                var line = Document.LineOf(_start);
                _start = Document.GetLineStart(line);
                _end = Document.GetLineEnd(line);
                break;
            }

            default:
                _start = 0;
                _end = Document.Length;
                break;
        }

        Clamp();
    }

    public ITextRangeProvider? FindAttribute(int attributeId, object value, bool backward) => null;

    public ITextRangeProvider? FindText(string text, bool backward, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var haystack = Document.GetText(_start, _end - _start);
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var index = backward ? haystack.LastIndexOf(text, comparison) : haystack.IndexOf(text, comparison);
        return index < 0
            ? null
            : new SqlEditorTextRange(_peer, _owner, _start + index, _start + index + text.Length);
    }

    public object? GetAttributeValue(int attributeId) => null;

    public void GetBoundingRectangles(out double[] rectangles) => rectangles = [];

    public IRawElementProviderSimple GetEnclosingElement() => _peer.GetRawElementProviderSimple();

    public string GetText(int maxLength)
    {
        var length = _end - _start;
        if (maxLength >= 0)
        {
            length = Math.Min(length, maxLength);
        }

        return Document.GetText(_start, Math.Max(0, length));
    }

    public int Move(TextUnit unit, int count)
    {
        var moved = MoveEndpointByUnit(TextPatternRangeEndpoint.Start, unit, count);
        _end = _start;
        ExpandToEnclosingUnit(unit);
        return moved;
    }

    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
    {
        var offset = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        var moved = 0;
        var direction = Math.Sign(count);

        for (var i = 0; i < Math.Abs(count); i++)
        {
            var next = Step(offset, unit, direction);
            if (next == offset)
            {
                break;
            }

            offset = next;
            moved += direction;
        }

        if (endpoint == TextPatternRangeEndpoint.Start)
        {
            _start = offset;
            _end = Math.Max(_end, _start);
        }
        else
        {
            _end = offset;
            _start = Math.Min(_start, _end);
        }

        Clamp();
        return moved;
    }

    private int Step(int offset, TextUnit unit, int direction)
    {
        if (direction == 0)
        {
            return offset;
        }

        switch (unit)
        {
            case TextUnit.Character:
                return Math.Clamp(offset + direction, 0, Document.Length);

            case TextUnit.Word:
                return Document.NextWordBoundary(offset, direction);

            case TextUnit.Line:
            case TextUnit.Paragraph:
            {
                var line = Math.Clamp(Document.LineOf(offset) + direction, 0, Document.LineCount - 1);
                return Document.GetLineStart(line);
            }

            default:
                return direction > 0 ? Document.Length : 0;
        }
    }

    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        if (targetRange is not SqlEditorTextRange other)
        {
            return;
        }

        var value = targetEndpoint == TextPatternRangeEndpoint.Start ? other._start : other._end;
        if (endpoint == TextPatternRangeEndpoint.Start)
        {
            _start = value;
            _end = Math.Max(_end, _start);
        }
        else
        {
            _end = value;
            _start = Math.Min(_start, _end);
        }

        Clamp();
    }

    public void Select()
    {
        _owner.SelectRange(_start, _end - _start);
        _peer.RaiseSelectionChanged();
    }

    public void AddToSelection()
    {
    }

    public void RemoveFromSelection()
    {
    }

    public void ScrollIntoView(bool alignToTop) => _owner.ScrollToLine(Document.LineOf(_start));

    public IRawElementProviderSimple[] GetChildren() => [];
}
