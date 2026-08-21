using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using SqlPlanViz.Editing;
using SqlPlanViz.ViewModels;

namespace SqlPlanViz.Views;

/// <summary>
/// The parameters strip under the editor (live-plan-editor-plan.md Phase 3).
///
/// Scalars are templated in XAML; table-valued parameters are built here, because their
/// shape is not known until a table type is chosen and there is no fixed template to write
/// for a grid whose columns arrive at runtime.
/// </summary>
public sealed partial class ParameterStrip : UserControl
{
    public ParameterStrip()
    {
        InitializeComponent();
        Parameters.CollectionChanged += OnParametersChanged;
        Refresh();
    }

    /// <summary>Raised whenever a value, type or row changes, so the host can mark the plan stale.</summary>
    public event EventHandler? BindingsChanged;

    public ObservableCollection<ParameterBindingItem> Parameters { get; } = [];

    public ObservableCollection<ParameterBindingItem> ScalarParameters { get; } = [];

    public bool HasParameters => Parameters.Count > 0;

    public bool AllValid => Parameters.All(p => p.IsValid);

    /// <summary>Bindings for <see cref="SqlBatchComposer"/>, in the order the batch mentions them.</summary>
    public IReadOnlyList<ParameterBinding> ToBindings() => [.. Parameters.Select(p => p.ToBinding())];

    /// <summary>
    /// Replaces the strip's contents from a fresh extraction, keeping any value the user has
    /// already typed for a parameter of the same name — retyping a value because you added a
    /// join is exactly the kind of thing that makes a tuning loop not worth using.
    /// </summary>
    public void SetParameters(IReadOnlyList<RequiredParameter> required)
    {
        var existing = Parameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        Parameters.CollectionChanged -= OnParametersChanged;
        Parameters.Clear();

        foreach (var parameter in required)
        {
            if (existing.TryGetValue(parameter.Name, out var previous)
                && previous.IsTableValued == parameter.IsTableValued)
            {
                Parameters.Add(previous);
                continue;
            }

            var item = new ParameterBindingItem(parameter);
            item.PropertyChanged += OnItemChanged;
            Parameters.Add(item);
        }

        Parameters.CollectionChanged += OnParametersChanged;
        Refresh();
        BindingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Fills a table-valued parameter's grid shape. Phase 6 feeds this from sys.table_types.</summary>
    public void SetTableTypeColumns(string parameterName, string tableTypeName, IEnumerable<TvpColumn> columns)
    {
        var item = Parameters.FirstOrDefault(p =>
            string.Equals(p.Name, parameterName, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        item.TableTypeName = tableTypeName;
        item.SetTableColumns(columns);
        Refresh();
    }

    public void ResetAllToPlanValues()
    {
        foreach (var parameter in Parameters)
        {
            parameter.ResetToPlanValue();
        }

        BindingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnParametersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Refresh();

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ParameterBindingItem.Value)
            or nameof(ParameterBindingItem.DataType)
            or nameof(ParameterBindingItem.IsNull)
            or nameof(ParameterBindingItem.TableTypeName))
        {
            BindingsChanged?.Invoke(this, EventArgs.Empty);
        }

        if (e.PropertyName is nameof(ParameterBindingItem.DataType))
        {
            RebuildTables();
        }
    }

    private void Refresh()
    {
        ScalarParameters.Clear();
        foreach (var parameter in Parameters.Where(p => p.IsScalar))
        {
            ScalarParameters.Add(parameter);
        }

        var count = Parameters.Count;
        HeaderText.Text = count == 0 ? "Parameters" : $"Parameters ({count})";

        var invalid = Parameters.Count(p => !p.IsValid);
        HintText.Text = count == 0
            ? "This batch needs none."
            : invalid > 0
                ? $"{invalid} need{(invalid == 1 ? "s" : string.Empty)} a valid value before the batch can be sent."
                : "Values are written into a DECLARE prelude at capture time.";

        ResetButton.IsEnabled = Parameters.Any(p => p.PlanCompiledValue is not null || p.PlanRuntimeValue is not null);

        // Collapse to nothing when there is nothing to show, per the plan.
        Root.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ApplyExpanded();
        RebuildTables();
    }

    private void ApplyExpanded()
    {
        var expanded = ExpandToggle.IsChecked == true;
        ItemsHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        TableHost.Visibility = expanded && TableHost.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExpandGlyph.Glyph = expanded ? "" : "";
    }

    private void OnToggleExpanded(object sender, RoutedEventArgs e) => ApplyExpanded();

    private void OnResetAll(object sender, RoutedEventArgs e) => ResetAllToPlanValues();

    // ---- Table-valued parameter grids --------------------------------------

    private void RebuildTables()
    {
        TableHost.Children.Clear();

        foreach (var parameter in Parameters.Where(p => p.IsTableValued))
        {
            TableHost.Children.Add(BuildTableCard(parameter));
        }

        TableHost.Visibility = ExpandToggle.IsChecked == true && TableHost.Children.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private Border BuildTableCard(ParameterBindingItem parameter)
    {
        var panel = new StackPanel { Spacing = 6 };

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = parameter.Name,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var typeBox = new TextBox
        {
            Text = parameter.TableTypeName,
            FontSize = 12,
            PlaceholderText = "table type, e.g. dbo.IdList",
            IsSpellCheckEnabled = false,
        };
        typeBox.TextChanged += (_, _) => parameter.TableTypeName = typeBox.Text;

        var addRow = new Button { Content = "Add row", FontSize = 11, Padding = new Thickness(8, 2, 8, 2) };
        addRow.Click += (_, _) =>
        {
            if (parameter.Columns.Count == 0)
            {
                // With no known shape there is nothing to add a row to; one text column lets
                // a single-column type (the common case: a list of ids) still be usable.
                parameter.SetTableColumns([new TvpColumn { Name = "Value", DataType = "nvarchar(100)" }]);
            }

            parameter.AddRow();
            RebuildTables();
            BindingsChanged?.Invoke(this, EventArgs.Empty);
        };

        Grid.SetColumn(typeBox, 1);
        Grid.SetColumn(addRow, 2);
        header.Children.Add(name);
        header.Children.Add(typeBox);
        header.Children.Add(addRow);
        panel.Children.Add(header);

        if (parameter.Columns.Count > 0)
        {
            panel.Children.Add(BuildGrid(parameter));
        }

        if (parameter.ValidationMessage is { Length: > 0 } message)
        {
            panel.Children.Add(new TextBlock
            {
                Text = message,
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            });
        }

        return new Border
        {
            Child = panel,
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["LayerFillColorAltBrush"],
        };
    }

    private Grid BuildGrid(ParameterBindingItem parameter)
    {
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 4 };

        foreach (var _ in parameter.Columns)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var c = 0; c < parameter.Columns.Count; c++)
        {
            var heading = new TextBlock
            {
                Text = $"{parameter.Columns[c].Name}  ·  {parameter.Columns[c].DataType}",
                FontSize = 11,
                Opacity = 0.7,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(heading, c);
            grid.Children.Add(heading);
        }

        for (var r = 0; r < parameter.Rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = parameter.Rows[r];

            for (var c = 0; c < parameter.Columns.Count && c < row.Cells.Count; c++)
            {
                var cell = row.Cells[c];
                var box = new TextBox
                {
                    Text = cell.Value,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 12,
                    IsSpellCheckEnabled = false,
                    PlaceholderText = cell.IsNull ? "NULL" : string.Empty,
                    IsEnabled = !cell.IsNull,
                };
                box.TextChanged += (_, _) =>
                {
                    cell.Value = box.Text;
                    BindingsChanged?.Invoke(this, EventArgs.Empty);
                };

                // The per-cell NULL toggle: the same explicit distinction the scalar rows
                // make, one level down, because an empty cell is an empty string.
                var nullToggle = new ToggleButton
                {
                    Content = "∅",
                    FontSize = 10,
                    MinWidth = 0,
                    Padding = new Thickness(5, 0, 5, 0),
                    IsChecked = cell.IsNull,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                ToolTipService.SetToolTip(nullToggle, "Bind this cell to NULL");
                nullToggle.Click += (_, _) =>
                {
                    cell.IsNull = nullToggle.IsChecked == true;
                    box.IsEnabled = !cell.IsNull;
                    box.PlaceholderText = cell.IsNull ? "NULL" : string.Empty;
                    BindingsChanged?.Invoke(this, EventArgs.Empty);
                };

                var cellPanel = new Grid { ColumnSpacing = 4 };
                cellPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                cellPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(nullToggle, 1);
                cellPanel.Children.Add(box);
                cellPanel.Children.Add(nullToggle);

                Grid.SetColumn(cellPanel, c);
                Grid.SetRow(cellPanel, r + 1);
                grid.Children.Add(cellPanel);
            }

            var remove = new Button
            {
                Content = "✕",
                FontSize = 11,
                Padding = new Thickness(6, 2, 6, 2),
            };

            var captured = row;
            remove.Click += (_, _) =>
            {
                parameter.RemoveRow(captured);
                RebuildTables();
                BindingsChanged?.Invoke(this, EventArgs.Empty);
            };

            Grid.SetColumn(remove, parameter.Columns.Count);
            Grid.SetRow(remove, r + 1);
            grid.Children.Add(remove);
        }

        return grid;
    }
}
