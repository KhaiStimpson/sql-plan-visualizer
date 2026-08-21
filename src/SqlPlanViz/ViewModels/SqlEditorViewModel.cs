using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SqlPlanViz.Capture;
using SqlPlanViz.Editing;
using SqlPlanViz.Model;

namespace SqlPlanViz.ViewModels;

/// <summary>
/// State for the SQL editor pane: its text, its parameters, whether a capture is in flight,
/// the last compile error, and whether the plan on screen still describes the text
/// (live-plan-editor-plan.md Phase 4).
///
/// Deliberately free of any WinUI type. The control renders it; the view model decides what
/// is true, including where a SQL error's line number lands once the composed batch's prelude
/// is subtracted back off.
/// </summary>
public sealed partial class SqlEditorViewModel : ObservableObject
{
    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStale;

    [ObservableProperty]
    private SqlParserVersion? _parserVersion;

    /// <summary>Every parameter the batch needs, in the order it mentions them.</summary>
    public ObservableCollection<ParameterBindingItem> Parameters { get; } = [];

    /// <summary>The scalar subset, which is what the strip's XAML template renders.</summary>
    public ObservableCollection<ParameterBindingItem> ScalarParameters { get; } = [];

    /// <summary>Squiggle ranges for the last compile error, in editor offsets.</summary>
    public IReadOnlyList<EditorSquiggle> Squiggles { get; private set; } = [];

    /// <summary>Statement the parameter types and the completion objects are drawn from.</summary>
    public PlanStatement? SourceStatement { get; private set; }

    /// <summary>The exact batch text of the last successful capture. Staleness is measured against it.</summary>
    public string? LastCapturedText { get; private set; }

    public bool HasParameters => Parameters.Count > 0;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool AllParametersValid => Parameters.All(p => p.IsValid);

    /// <summary>Re-planning needs text, valid parameters, and nothing already in flight.</summary>
    public bool CanReplan => !IsBusy
                             && !string.IsNullOrWhiteSpace(Text)
                             && AllParametersValid;

    /// <summary>Raised when the parameter set or a binding changes, so the host can recompose.</summary>
    public event EventHandler? ParametersChanged;

    /// <summary>
    /// Points the editor at a statement: its text becomes the editor's text unless the user
    /// has unsaved edits, and its ParameterList becomes the type source for extraction.
    /// </summary>
    public void SetSourceStatement(PlanStatement? statement, bool replaceText)
    {
        SourceStatement = statement;

        if (replaceText)
        {
            Text = statement?.Summary.StatementText ?? string.Empty;
            LastCapturedText = Text;
            IsStale = false;
            ClearError();
        }

        RefreshParameters();
    }

    /// <summary>
    /// Re-extracts the batch's parameters, keeping the values already entered for names that
    /// survived the edit. Cheap enough to call on every keystroke — it is a parse, not a round
    /// trip — but the host debounces it anyway.
    /// </summary>
    public void RefreshParameters()
    {
        var required = SqlParameterExtractor.Extract(Text, SourceStatement, ParserVersion);
        var existing = Parameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in Parameters)
        {
            parameter.PropertyChanged -= OnParameterChanged;
        }

        Parameters.Clear();

        foreach (var parameter in required)
        {
            var item = existing.TryGetValue(parameter.Name, out var previous)
                       && previous.IsTableValued == parameter.IsTableValued
                ? previous
                : new ParameterBindingItem(parameter);

            item.PropertyChanged += OnParameterChanged;
            Parameters.Add(item);
        }

        ScalarParameters.Clear();
        foreach (var parameter in Parameters.Where(p => p.IsScalar))
        {
            ScalarParameters.Add(parameter);
        }

        OnPropertyChanged(nameof(HasParameters));
        OnPropertyChanged(nameof(AllParametersValid));
        OnPropertyChanged(nameof(CanReplan));
        ParametersChanged?.Invoke(this, EventArgs.Empty);
    }

    public ComposedBatch Compose() => SqlBatchComposer.Compose(Text, [.. Parameters.Select(p => p.ToBinding())]);

    public void ResetParametersToPlanValues()
    {
        foreach (var parameter in Parameters)
        {
            parameter.ResetToPlanValue();
        }
    }

    /// <summary>Records a successful capture: the text is now what the plan on screen describes.</summary>
    public void MarkCaptured(string capturedText)
    {
        LastCapturedText = capturedText;
        IsStale = false;
        ClearError();
        StatusMessage = "Plan captured for the current text.";
    }

    public void ClearError()
    {
        ErrorMessage = null;
        Squiggles = [];
        OnPropertyChanged(nameof(Squiggles));
        OnPropertyChanged(nameof(HasError));
    }

    /// <summary>
    /// Turns a capture failure into something the editor can draw. SQL Server reports a line
    /// within the batch it was sent, so the prelude has to come back off before the number
    /// means anything — an error on the first line of the user's text is line 1 to them and
    /// line 4 to the server.
    /// </summary>
    public void ApplyCaptureError(PlanCaptureException exception, ComposedBatch batch)
    {
        var squiggles = new List<EditorSquiggle>();
        var messages = new List<string>();

        foreach (var error in exception.Errors)
        {
            var message = error.LineNumber > 0 && !batch.IsPreludeLine(error.LineNumber)
                ? $"Line {batch.ToEditorLine(error.LineNumber) + 1}: {error.Message}"
                : error.Message;

            if (!messages.Contains(message, StringComparer.Ordinal))
            {
                messages.Add(message);
            }

            if (error.LineNumber <= 0 || batch.IsPreludeLine(error.LineNumber))
            {
                continue;
            }

            var line = batch.ToEditorLine(error.LineNumber);
            var (start, length) = LineRange(line);
            if (length > 0 && !squiggles.Any(s => s.Start == start))
            {
                squiggles.Add(new EditorSquiggle { Start = start, Length = length, Message = error.Message });
            }
        }

        Squiggles = squiggles;
        ErrorMessage = messages.Count > 0 ? string.Join(Environment.NewLine, messages) : exception.Message;
        StatusMessage = squiggles.Count > 0
            ? $"SQL Server rejected the batch at line {LineOf(squiggles[0].Start) + 1}."
            : "SQL Server rejected the batch.";

        OnPropertyChanged(nameof(Squiggles));
        OnPropertyChanged(nameof(HasError));
    }

    /// <summary>Reports a failure that has no line at all — a composition error or a dead connection.</summary>
    public void ApplyError(string message)
    {
        Squiggles = [];
        ErrorMessage = message;
        StatusMessage = message;
        OnPropertyChanged(nameof(Squiggles));
        OnPropertyChanged(nameof(HasError));
    }

    /// <summary>Start and length of a zero-based line in <see cref="Text"/>, excluding its newline.</summary>
    private (int Start, int Length) LineRange(int line)
    {
        var start = 0;
        var current = 0;

        while (current < line && start < Text.Length)
        {
            var next = Text.IndexOf('\n', start);
            if (next < 0)
            {
                return (Math.Max(0, Text.Length - 1), 1);
            }

            start = next + 1;
            current++;
        }

        var end = Text.IndexOf('\n', start);
        if (end < 0)
        {
            end = Text.Length;
        }

        while (end > start && (Text[end - 1] == '\r' || Text[end - 1] == '\n'))
        {
            end--;
        }

        // A blank line still needs somewhere to draw, or the error would be invisible.
        return (start, Math.Max(1, end - start));
    }

    private int LineOf(int offset)
    {
        var line = 0;
        for (var i = 0; i < offset && i < Text.Length; i++)
        {
            if (Text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private void OnParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ParameterBindingItem.Value)
            or nameof(ParameterBindingItem.DataType)
            or nameof(ParameterBindingItem.IsNull)
            or nameof(ParameterBindingItem.TableTypeName))
        {
            // A parameter change alters the batch as surely as an edit does, so it makes the
            // plan on screen stale too.
            IsStale = true;
            OnPropertyChanged(nameof(AllParametersValid));
            OnPropertyChanged(nameof(CanReplan));
            ParametersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    partial void OnTextChanged(string value)
    {
        IsStale = LastCapturedText is not null && !string.Equals(LastCapturedText, value, StringComparison.Ordinal);
        OnPropertyChanged(nameof(CanReplan));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanReplan));

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));
}
