using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SqlPlanViz.Editing;

namespace SqlPlanViz.ViewModels;

/// <summary>Which editor the parameter strip shows for a parameter's value.</summary>
public enum ParameterEditorKind
{
    Text,
    Numeric,
    DateTime,
    Guid,
    Bit,
    Binary,
    Table,
}

/// <summary>One row of a table-valued parameter's grid. Cells are text; typing happens per column.</summary>
public sealed partial class TvpRowItem : ObservableObject
{
    public ObservableCollection<TvpCellItem> Cells { get; } = [];
}

public sealed partial class TvpCellItem : ObservableObject
{
    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private bool _isNull;

    public string ColumnName { get; init; } = string.Empty;

    public string DataType { get; init; } = "nvarchar(100)";

    /// <summary>Null means NULL; the empty string means an empty string. The composer relies on it.</summary>
    public string? EffectiveValue => IsNull ? null : Value;
}

/// <summary>
/// One parameter in the strip under the editor (live-plan-editor-plan.md Phase 3).
///
/// Prefilled from the plan's own ParameterList where there is one, which is the whole point:
/// re-planning a parameterised query should not start with typing out its parameters, and the
/// values SQL Server compiled the plan for are the ones that reproduce it.
/// </summary>
public sealed partial class ParameterBindingItem : ObservableObject
{
    [ObservableProperty]
    private string _dataType = "nvarchar(100)";

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private bool _isNull;

    [ObservableProperty]
    private string _tableTypeName = string.Empty;

    [ObservableProperty]
    private string? _validationMessage;

    /// <summary>Position within the parameter strip's scalar rows — drives the alternating row tint.</summary>
    [ObservableProperty]
    private int _rowIndex;

    public ParameterBindingItem(RequiredParameter parameter)
    {
        Name = parameter.Name;
        DataType = parameter.DataType;
        TypeSource = parameter.TypeSource;
        IsTableValued = parameter.IsTableValued;
        PlanCompiledValue = parameter.PlanCompiledValue;
        PlanRuntimeValue = parameter.PlanRuntimeValue;

        if (IsTableValued)
        {
            TableTypeName = parameter.DataType;
        }

        // The runtime value is what the query really ran with; the compiled value is what the
        // optimizer chose the plan for. Prefer runtime, since that is the case being tuned —
        // and note when they differ, because that difference is parameter sniffing.
        var prefill = parameter.PlanRuntimeValue ?? parameter.PlanCompiledValue;
        if (prefill is not null)
        {
            Value = prefill;
        }
        else if (parameter is { PlanCompiledValue: null, PlanRuntimeValue: null }
                 && HasPlanEntry(parameter))
        {
            IsNull = true;
        }

        Validate();
    }

    public string Name { get; }

    public ParameterTypeSource TypeSource { get; }

    public bool IsTableValued { get; }

    public string? PlanCompiledValue { get; }

    public string? PlanRuntimeValue { get; }

    public ObservableCollection<TvpColumnItem> Columns { get; } = [];

    public ObservableCollection<TvpRowItem> Rows { get; } = [];

    public bool IsValid => ValidationMessage is null;

    public bool IsScalar => !IsTableValued;

    /// <summary>Set when the plan's compiled and runtime values differ — the sniffing signal.</summary>
    public bool WasSniffed => PlanCompiledValue is not null
                              && PlanRuntimeValue is not null
                              && !string.Equals(PlanCompiledValue, PlanRuntimeValue, StringComparison.Ordinal);

    public string TypeSourceHint => TypeSource switch
    {
        ParameterTypeSource.Plan => "type from the plan",
        ParameterTypeSource.Inferred => "type inferred from the query — check it",
        _ => "type guessed — set it",
    };

    public ParameterEditorKind EditorKind
    {
        get
        {
            if (IsTableValued) return ParameterEditorKind.Table;
            if (SqlLiteral.IsBit(DataType)) return ParameterEditorKind.Bit;
            if (SqlLiteral.IsNumeric(DataType)) return ParameterEditorKind.Numeric;
            if (SqlLiteral.IsDateTime(DataType)) return ParameterEditorKind.DateTime;
            if (SqlLiteral.IsGuid(DataType)) return ParameterEditorKind.Guid;
            if (SqlLiteral.IsBinary(DataType)) return ParameterEditorKind.Binary;
            return ParameterEditorKind.Text;
        }
    }

    /// <summary>
    /// The date portion of a DateTime value's text, composed with <see cref="TimeText"/> into
    /// <see cref="Value"/> — the string <c>SqlLiteral.Format</c> parses all the way through.
    /// </summary>
    public DateTimeOffset? DateValue
    {
        get => DateTimeOffset.TryParse(Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
        set
        {
            var time = TimeText;
            Value = value is { } date ? $"{date:yyyy-MM-dd} {time}" : string.Empty;
            OnPropertyChanged(nameof(TimeText));
        }
    }

    /// <summary>The time-of-day portion of a DateTime value's text, composed with <see cref="DateValue"/>.</summary>
    public string TimeText
    {
        get => DateTimeOffset.TryParse(Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : string.Empty;
        set
        {
            var date = DateValue ?? DateTimeOffset.Now;
            Value = $"{date:yyyy-MM-dd} {value}";
            OnPropertyChanged(nameof(DateValue));
        }
    }

    /// <summary>Replaces the grid's shape, keeping any rows that still fit (Phase 6 fills this from sys.table_types).</summary>
    public void SetTableColumns(IEnumerable<TvpColumn> columns)
    {
        Columns.Clear();
        foreach (var column in columns)
        {
            Columns.Add(new TvpColumnItem { Name = column.Name, DataType = column.DataType });
        }

        foreach (var row in Rows)
        {
            SyncRow(row);
        }

        Validate();
    }

    public void AddRow()
    {
        var row = new TvpRowItem();
        SyncRow(row);
        Rows.Add(row);
        Validate();
    }

    public void RemoveRow(TvpRowItem row)
    {
        Rows.Remove(row);
        Validate();
    }

    private void SyncRow(TvpRowItem row)
    {
        while (row.Cells.Count > Columns.Count)
        {
            row.Cells.RemoveAt(row.Cells.Count - 1);
        }

        for (var i = 0; i < Columns.Count; i++)
        {
            if (i < row.Cells.Count)
            {
                continue;
            }

            row.Cells.Add(new TvpCellItem { ColumnName = Columns[i].Name, DataType = Columns[i].DataType });
        }
    }

    /// <summary>Resets to the value the plan carries, undoing whatever was typed over it.</summary>
    public void ResetToPlanValue()
    {
        var prefill = PlanRuntimeValue ?? PlanCompiledValue;
        Value = prefill ?? string.Empty;
        IsNull = prefill is null;
        Validate();
    }

    public ParameterBinding ToBinding() => new()
    {
        Name = Name,
        DataType = DataType,
        Value = Value,
        IsNull = IsNull,
        IsTableValued = IsTableValued,
        TableTypeName = TableTypeName,
        Columns = [.. Columns.Select(c => new TvpColumn { Name = c.Name, DataType = c.DataType })],
        Rows = [.. Rows.Select(r => (IReadOnlyList<string?>)[.. r.Cells.Select(c => c.EffectiveValue)])],
    };

    /// <summary>
    /// Validates by composing the literal the batch would actually carry, so the strip cannot
    /// say a value is fine and then have the composer reject it.
    /// </summary>
    public void Validate()
    {
        if (IsTableValued)
        {
            ValidationMessage = string.IsNullOrWhiteSpace(TableTypeName)
                ? "Choose the table type this parameter uses."
                : null;
            OnPropertyChanged(nameof(IsValid));
            return;
        }

        var literal = SqlLiteral.Format(Value, DataType, IsNull);
        ValidationMessage = literal.IsValid ? null : literal.Error;
        OnPropertyChanged(nameof(IsValid));
    }

    private static bool HasPlanEntry(RequiredParameter parameter) =>
        parameter.TypeSource == ParameterTypeSource.Plan;

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(DateValue));
        OnPropertyChanged(nameof(TimeText));
        Validate();
    }

    partial void OnIsNullChanged(bool value) => Validate();

    partial void OnTableTypeNameChanged(string value) => Validate();

    partial void OnDataTypeChanged(string value)
    {
        OnPropertyChanged(nameof(EditorKind));
        Validate();
    }
}

public sealed partial class TvpColumnItem : ObservableObject
{
    public string Name { get; init; } = string.Empty;

    public string DataType { get; init; } = "nvarchar(100)";
}
