using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using SqlPlanViz.Editing.Completion;
using Windows.Foundation;

namespace SqlPlanViz.Controls;

/// <summary>
/// The completion list: a native WinUI <see cref="Popup"/> over a <see cref="ListView"/>,
/// positioned from the editor's caret rectangle (live-plan-editor-plan.md Phase 2).
///
/// Rows are built in code rather than from a XAML template because the list is rebuilt on
/// every keystroke and the item count is small — a template plus item containers costs more
/// than it saves at this size, and the glyph colour has to vary per row anyway.
///
/// The popup never takes focus. The editor keeps it, so composition input is not interrupted
/// and the arrow keys reach the list only because the editor forwards them.
/// </summary>
public sealed class CompletionPopup
{
    private const double PopupWidth = 380;
    private const double MaxListHeight = 240;

    private readonly Popup _popup = new() { IsLightDismissEnabled = false };
    private readonly ListView _list = new()
    {
        SelectionMode = ListViewSelectionMode.Single,
        IsItemClickEnabled = true,
        MaxHeight = MaxListHeight,
        Padding = new Thickness(2),
    };

    private readonly TextBlock _documentation = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        MaxLines = 3,
        Margin = new Thickness(10, 6, 10, 8),
    };

    private readonly Border _documentationBorder;
    private IReadOnlyList<CompletionItem> _items = [];

    public CompletionPopup()
    {
        _documentationBorder = new Border
        {
            Child = _documentation,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        var panel = new StackPanel();
        panel.Children.Add(_list);
        panel.Children.Add(_documentationBorder);

        var root = new Border
        {
            Width = PopupWidth,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = panel,
        };

        root.Background = (Brush)Application.Current.Resources["AcrylicBackgroundFillColorDefaultBrush"];
        root.BorderBrush = (Brush)Application.Current.Resources["SurfaceStrokeColorFlyoutBrush"];
        _documentationBorder.BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];

        _popup.Child = root;
        _list.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is ListViewItem { Tag: CompletionItem item })
            {
                Committed?.Invoke(this, item);
            }
        };

        _list.SelectionChanged += (_, _) => UpdateDocumentation();
    }

    /// <summary>Raised when the user clicks a row. Keyboard acceptance goes through the editor.</summary>
    public event EventHandler<CompletionItem>? Committed;

    public bool IsOpen => _popup.IsOpen;

    public CompletionItem? SelectedItem =>
        _list.SelectedItem is ListViewItem { Tag: CompletionItem item } ? item : null;

    /// <summary>
    /// Shows or updates the list. <paramref name="caretInRoot"/> is the caret rectangle in
    /// the XamlRoot's coordinates, so the popup can be flipped above the caret when there is
    /// no room below it.
    /// </summary>
    public void Show(XamlRoot xamlRoot, IReadOnlyList<CompletionItem> items, Rect caretInRoot)
    {
        if (items.Count == 0)
        {
            Hide();
            return;
        }

        _items = items;
        _popup.XamlRoot = xamlRoot;
        Rebuild();

        var estimatedHeight = Math.Min(MaxListHeight, (items.Count * 26) + 8) + 44;
        var below = caretInRoot.Bottom + 2;
        var flip = below + estimatedHeight > xamlRoot.Size.Height && caretInRoot.Top > estimatedHeight;

        _popup.HorizontalOffset = Math.Max(
            0,
            Math.Min(caretInRoot.Left, xamlRoot.Size.Width - PopupWidth - 8));
        _popup.VerticalOffset = flip ? Math.Max(0, caretInRoot.Top - estimatedHeight - 2) : below;
        _popup.IsOpen = true;
    }

    public void Hide()
    {
        _popup.IsOpen = false;
        _list.Items.Clear();
        _items = [];
    }

    /// <summary>Moves the highlight, wrapping at both ends the way every completion list does.</summary>
    public void MoveSelection(int delta)
    {
        if (_list.Items.Count == 0)
        {
            return;
        }

        var index = _list.SelectedIndex < 0 ? 0 : _list.SelectedIndex + delta;
        if (index < 0)
        {
            index = _list.Items.Count - 1;
        }
        else if (index >= _list.Items.Count)
        {
            index = 0;
        }

        _list.SelectedIndex = index;
        _list.ScrollIntoView(_list.Items[index]);
    }

    private void Rebuild()
    {
        _list.Items.Clear();

        foreach (var item in _items)
        {
            _list.Items.Add(BuildRow(item));
        }

        if (_list.Items.Count > 0)
        {
            _list.SelectedIndex = 0;
        }

        UpdateDocumentation();
    }

    private ListViewItem BuildRow(CompletionItem item)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var glyph = new TextBlock
        {
            Text = item.KindGlyph,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11,
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var label = new TextBlock
        {
            Text = item.Label,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // A tuning suggestion is not a name you asked for — it has to look different or it
        // reads as the schema offering a column that does not exist.
        if (item.IsSuggestion)
        {
            label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            label.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
            glyph.Foreground = label.Foreground;
        }

        var detail = new TextBlock
        {
            Text = item.Detail,
            FontSize = 11,
            Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(glyph, 0);
        Grid.SetColumn(label, 1);
        Grid.SetColumn(detail, 2);
        grid.Children.Add(glyph);
        grid.Children.Add(label);
        grid.Children.Add(detail);

        return new ListViewItem
        {
            Content = grid,
            Tag = item,
            Padding = new Thickness(8, 2, 8, 2),
            MinHeight = 26,
        };
    }

    private void UpdateDocumentation()
    {
        var documentation = SelectedItem?.Documentation ?? string.Empty;
        _documentation.Text = documentation;
        _documentationBorder.Visibility = string.IsNullOrWhiteSpace(documentation)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
