using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SqlPlanViz.Themes;
using Windows.System;

namespace SqlPlanViz.Controls;

/// <summary>One palette entry: a group ("Plan", "View", "Operators"), a label and what it does.</summary>
public sealed record PaletteCommand(string Group, string Label, Action Run, string? Hint = null);

/// <summary>
/// The Ctrl+K command palette (Gauge COMPONENTS.md). Typing filters and makes the first match
/// active; Up and Down move the active option and wrap; Enter runs it; Escape or a click on the
/// scrim closes. Tab does not leave it. Closing returns focus to whatever opened it.
/// </summary>
public sealed partial class CommandPalette : UserControl
{
    private IReadOnlyList<PaletteCommand> _commands = [];
    private List<PaletteCommand> _matches = [];
    private readonly List<Border> _rows = [];
    private int _active;
    private DependencyObject? _opener;

    public CommandPalette()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => Render();
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Opens over the page with <paramref name="commands"/>, remembering what had focus.</summary>
    public void Open(IReadOnlyList<PaletteCommand> commands)
    {
        _commands = commands;
        _opener = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        Visibility = Visibility.Visible;
        Input.Text = string.Empty;
        Filter();
        Input.Focus(FocusState.Keyboard);
    }

    /// <summary>Closes, returning focus to the opener unless a command moved it on purpose.</summary>
    public void Close(bool restoreFocus = true)
    {
        Visibility = Visibility.Collapsed;
        if (restoreFocus && _opener is Control control && control.IsLoaded)
        {
            control.Focus(FocusState.Keyboard);
        }
        else if (restoreFocus && _opener is UIElement element)
        {
            element.Focus(FocusState.Keyboard);
        }

        _opener = null;
    }

    private void OnScrimTapped(object sender, TappedRoutedEventArgs e) => Close();

    private void OnInputTextChanged(object sender, TextChangedEventArgs e) => Filter();

    private void Filter()
    {
        var query = Input.Text.Trim();
        _matches = query.Length == 0
            ? [.. _commands]
            : [.. _commands.Where(c => c.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                                    || c.Group.Contains(query, StringComparison.OrdinalIgnoreCase))];
        _active = 0;
        Render();
    }

    private void Render()
    {
        if (!IsLoaded && Visibility != Visibility.Visible)
        {
            return;
        }

        ListHost.Children.Clear();
        _rows.Clear();
        var tokens = GaugeTokens.For(ActualTheme);

        if (_matches.Count == 0)
        {
            ListHost.Children.Add(new TextBlock
            {
                Text = "No command or operator matches.",
                Margin = new Thickness(12, 8, 12, 8),
                Foreground = new SolidColorBrush(tokens.Fg2),
            });
            Announcer.Text = "No matches";
            return;
        }

        string? group = null;
        for (var i = 0; i < _matches.Count; i++)
        {
            var command = _matches[i];
            if (command.Group != group)
            {
                group = command.Group;
                ListHost.Children.Add(new TextBlock
                {
                    Text = group,
                    FontSize = 12,
                    Margin = new Thickness(10, i == 0 ? 4 : 8, 10, 2),
                    Foreground = new SolidColorBrush(tokens.Fg2),
                });
            }

            var row = BuildRow(command, i == _active, tokens);
            var index = i;
            row.Tapped += (_, _) =>
            {
                _active = index;
                RunActive();
            };
            _rows.Add(row);
            ListHost.Children.Add(row);
        }

        UpdateAnnouncement();
    }

    /// <summary>An option; the active one is drawn as the selected record (sel, sel-fg, 2px sel-edge).</summary>
    private static Border BuildRow(PaletteCommand command, bool active, GaugeTokenSet tokens)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = command.Label,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(active ? tokens.SelFg : tokens.Fg1),
        });

        if (!string.IsNullOrEmpty(command.Hint))
        {
            var hint = new TextBlock
            {
                Text = command.Hint,
                FontSize = 12,
                Foreground = new SolidColorBrush(active ? tokens.SelFg : tokens.Fg2),
            };
            Grid.SetColumn(hint, 1);
            grid.Children.Add(hint);
        }

        var row = new Border
        {
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(active ? tokens.Sel : Microsoft.UI.Colors.Transparent),
            BorderBrush = new SolidColorBrush(active ? tokens.SelEdge : Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(active ? 2 : 0, 0, 0, 0),
            Child = grid,
        };
        AutomationProperties.SetName(row, $"{command.Group}: {command.Label}");
        return row;
    }

    private void MoveActive(int delta)
    {
        if (_matches.Count == 0)
        {
            return;
        }

        _active = (_active + delta + _matches.Count) % _matches.Count;
        Render();
        if (_active < _rows.Count)
        {
            _rows[_active].StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        }
    }

    private void UpdateAnnouncement()
    {
        if (_active < _matches.Count)
        {
            Announcer.Text = $"{_matches[_active].Label}, {_matches[_active].Group}, {_active + 1} of {_matches.Count}";
            if (FrameworkElementAutomationPeer.FromElement(Announcer) is { } peer)
            {
                peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
        }
    }

    private void RunActive()
    {
        if (_active >= _matches.Count)
        {
            return;
        }

        var command = _matches[_active];
        Close();
        command.Run();
    }

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                MoveActive(1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                MoveActive(-1);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                RunActive();
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                Close();
                e.Handled = true;
                break;
            case VirtualKey.Tab:
                // Tab does not leave the dialog (COMPONENTS.md).
                e.Handled = true;
                break;
        }
    }
}
