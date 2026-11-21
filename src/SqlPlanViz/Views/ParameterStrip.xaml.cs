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
/// A view over <see cref="SqlEditorViewModel"/>, which owns the bindings — the composer needs
/// them at capture time and the view model is what decides when the batch is sendable, so the
/// strip holding its own copy would be a second source of truth.
///
/// Scalars are templated in XAML; table-valued parameters are built here, because their shape
/// is not known until a table type is chosen and there is no fixed template to write for a
/// grid whose columns arrive at runtime.
/// </summary>
public sealed partial class ParameterStrip : UserControl
{
    private SqlEditorViewModel? _viewModel;

    public ParameterStrip()
    {
        InitializeComponent();

        // The table grid is built in code from GaugeTokens, so it is rebuilt with the theme.
        ActualThemeChanged += (_, _) => RebuildTables();
    }

    /// <summary>Raised when a row is added or removed, so the host can recompose and mark stale.</summary>
    public event EventHandler? BindingsChanged;

    public void Bind(SqlEditorViewModel viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.ParametersChanged -= OnParametersChanged;
        }

        _viewModel = viewModel;
        _viewModel.ParametersChanged += OnParametersChanged;
        ItemsHost.ItemsSource = viewModel.ScalarParameters;
        Refresh();
    }

    /// <summary>Fills a table-valued parameter's grid shape. Phase 6 feeds this from sys.table_types.</summary>
    public void SetTableTypeColumns(string parameterName, string tableTypeName, IEnumerable<TvpColumn> columns)
    {
        var item = _viewModel?.Parameters.FirstOrDefault(p =>
            string.Equals(p.Name, parameterName, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        item.TableTypeName = tableTypeName;
        item.SetTableColumns(columns);
        Refresh();
    }

    private void OnParametersChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var parameters = _viewModel?.Parameters;
        var count = parameters?.Count ?? 0;

        HeaderText.Text = "Parameters";
        CountText.Text = count == 0 ? string.Empty : count.ToString(System.Globalization.CultureInfo.CurrentCulture);

        var invalid = parameters?.Count(p => !p.IsValid) ?? 0;
        HintText.Text = count == 0
            ? "This batch needs none."
            : invalid > 0
                ? $"{invalid} need{(invalid == 1 ? "s" : string.Empty)} a valid value before the batch can be sent."
                : "Values are written into a DECLARE prelude at capture time.";

        ResetButton.IsEnabled = parameters?.Any(p => p.PlanCompiledValue is not null || p.PlanRuntimeValue is not null) == true;

        // Collapse to nothing when there is nothing to show, per the plan: an unparameterised
        // query should cost no vertical space in a pane already competing with the plan for it.
        Root.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ApplyExpanded();
        RebuildTables();
    }

    private void ApplyExpanded()
    {
        var expanded = ExpandToggle.IsChecked == true;
        ItemsHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ColumnHeadings.Visibility = expanded && _viewModel?.Parameters.Any(p => !p.IsTableValued) == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        TableHost.Visibility = expanded && TableHost.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // ChevronDown / ChevronRight in Segoe Fluent Icons.
        ExpandGlyph.Glyph = expanded ? "\uE70D" : "\uE76C";
    }

    private void OnToggleExpanded(object sender, RoutedEventArgs e) => ApplyExpanded();

    private void OnDataTypeGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is AutoSuggestBox { ItemsSource: null } box)
        {
            box.ItemsSource = ParameterTypeCatalog.CommonTypes;
        }
    }

    private void OnDataTypeTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        var text = sender.Text;
        sender.ItemsSource = string.IsNullOrEmpty(text)
            ? ParameterTypeCatalog.CommonTypes
            : ParameterTypeCatalog.CommonTypes.Where(t => t.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void OnDataTypeSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string text)
        {
            sender.Text = text;
        }
    }

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        _viewModel?.ResetParametersToPlanValues();
        BindingsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Table-valued parameter grids --------------------------------------

    private void RebuildTables()
    {
        TableHost.Children.Clear();

        foreach (var parameter in _viewModel?.Parameters.Where(p => p.IsTableValued) ?? [])
        {
            TableHost.Children.Add(BuildTableCard(parameter));
        }

        TableHost.Visibility = ExpandToggle.IsChecked == true && TableHost.Children.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>Gauge colours for the code-built table grid, in this control's theme.</summary>
    private Themes.GaugeTokenSet Tokens => Themes.GaugeTokens.For(ActualTheme);

    private Border BuildTableCard(ParameterBindingItem parameter)
    {
        var panel = new StackPanel { Spacing = 6 };

        var header = new Grid { ColumnSpacing = 10, Padding = new Thickness(10, 6, 10, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = parameter.Name,
            FontFamily = Themes.GaugeFonts.Mono,
            FontSize = 12,
            Foreground = new SolidColorBrush(Tokens.Code),
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

        var addRow = new Button { Content = "Add row", FontSize = 12.5, Style = (Style)Application.Current.Resources["GaugeGhostButton"] };
        addRow.Click += (_, _) =>
        {
            if (parameter.Columns.Count == 0)
            {
                // With no known shape there is nothing to add a row to; one text column keeps
                // the common case — a list of ids — usable with no catalog connected.
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
                FontSize = 12,
                Foreground = new SolidColorBrush(Tokens.Crit),
            });
        }

        // The GaugeTable treatment: a line-bordered block, no tinted card fill.
        return new Border
        {
            Child = panel,
            Padding = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Tokens.Line),
        };
    }

    private Grid BuildGrid(ParameterBindingItem parameter)
    {
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 4, Padding = new Thickness(10, 0, 10, 0) };

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
                Text = $"{parameter.Columns[c].Name} · {parameter.Columns[c].DataType}",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Foreground = new SolidColorBrush(Tokens.Fg2),
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
                    FontFamily = Themes.GaugeFonts.Mono,
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
                Content = new FontIcon { Glyph = "\uE711", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 11 },
                Style = (Style)Application.Current.Resources["GaugeIconButton"],
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, "Remove row");
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
