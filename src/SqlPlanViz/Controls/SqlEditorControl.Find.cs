using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace SqlPlanViz.Controls;

/// <summary>
/// The Ctrl+F find overlay (docs/editor-and-parameters-ux-plan.md Phase 5): a small XAML toolbar
/// floated over the Win2D canvas — not itself drawn immediate-mode, since a search box is exactly
/// the kind of control XAML already gives us for free — backed by <see cref="SqlDocument.FindAll"/>
/// and a highlight-all-matches pass in <c>OnDraw</c>.
/// </summary>
public sealed partial class SqlEditorControl
{
    private readonly TextBox _findBox = new()
    {
        Width = 160,
        FontSize = 12,
        PlaceholderText = "Find",
        IsSpellCheckEnabled = false,
    };

    private readonly TextBlock _findStatus = new()
    {
        FontSize = 11,
        MinWidth = 46,
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0.7,
    };

    private readonly Button _findPreviousButton = new() { Content = "▲", FontSize = 10, Padding = new Thickness(6, 4, 6, 4), MinWidth = 0 };
    private readonly Button _findNextButton = new() { Content = "▼", FontSize = 10, Padding = new Thickness(6, 4, 6, 4), MinWidth = 0 };
    private readonly Button _findCloseButton = new() { Content = "✕", FontSize = 10, Padding = new Thickness(6, 4, 6, 4), MinWidth = 0 };

    private Border _findOverlay = null!;
    private IReadOnlyList<int> _findMatches = [];
    private int _findMatchIndex = -1;

    private Border BuildFindOverlay()
    {
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        toolbar.Children.Add(_findBox);
        toolbar.Children.Add(_findStatus);
        toolbar.Children.Add(_findPreviousButton);
        toolbar.Children.Add(_findNextButton);
        toolbar.Children.Add(_findCloseButton);

        _findOverlay = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xD8, 0xDC, 0xE2)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 8, 20, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Child = toolbar,
        };
        _findOverlay.SetValue(Grid.ColumnProperty, 0);
        _findOverlay.SetValue(Grid.RowProperty, 0);

        // Themed at build time rather than via ThemeResource, matching how the rest of this
        // control mirrors its palette from SqlSyntaxTheme (Win2D draws outside the XAML resource
        // system, so this one XAML island keeps its own light/dark pair in step by hand).
        ActualThemeChanged += (_, _) => ApplyFindOverlayTheme();
        Loaded += (_, _) => ApplyFindOverlayTheme();

        _findBox.TextChanged += (_, _) => RunFindSearch();
        _findBox.KeyDown += OnFindBoxKeyDown;
        _findPreviousButton.Click += (_, _) => StepFindMatch(-1);
        _findNextButton.Click += (_, _) => StepFindMatch(1);
        _findCloseButton.Click += (_, _) => CloseFind();

        return _findOverlay;
    }

    private void ApplyFindOverlayTheme()
    {
        var dark = ActualTheme == ElementTheme.Dark;
        _findOverlay.Background = new SolidColorBrush(dark
            ? Microsoft.UI.ColorHelper.FromArgb(255, 0x24, 0x25, 0x29)
            : Microsoft.UI.ColorHelper.FromArgb(255, 0xFF, 0xFF, 0xFF));
        _findOverlay.BorderBrush = new SolidColorBrush(dark
            ? Microsoft.UI.ColorHelper.FromArgb(255, 0x3A, 0x3E, 0x46)
            : Microsoft.UI.ColorHelper.FromArgb(255, 0xD8, 0xDC, 0xE2));
    }

    /// <summary>Opens the overlay, seeded from the current selection when there is a short one.</summary>
    private void OpenFind()
    {
        if (HasSelection && SelectionLength is > 0 and < 200)
        {
            _findBox.Text = SelectedText;
        }

        _findOverlay.Visibility = Visibility.Visible;
        _findBox.Focus(FocusState.Programmatic);
        _findBox.SelectAll();
        RunFindSearch();
    }

    private void CloseFind()
    {
        _findOverlay.Visibility = Visibility.Collapsed;
        _findMatches = [];
        _findMatchIndex = -1;
        Focus(FocusState.Programmatic);
        Redraw();
    }

    private void OnFindBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                CloseFind();
                e.Handled = true;
                break;

            case VirtualKey.Enter:
                StepFindMatch(IsDown(VirtualKey.Shift) ? -1 : 1);
                e.Handled = true;
                break;
        }
    }

    private void RunFindSearch()
    {
        var query = _findBox.Text;
        _findMatches = _document.FindAll(query);

        if (_findMatches.Count == 0)
        {
            _findMatchIndex = -1;
            _findStatus.Text = string.IsNullOrEmpty(query) ? string.Empty : "0/0";
            Redraw();
            return;
        }

        // Land on the match at or after the caret, so search continues from where you're looking
        // rather than always jumping back to the top of the document.
        _findMatchIndex = 0;
        for (var i = 0; i < _findMatches.Count; i++)
        {
            if (_findMatches[i] >= _caret)
            {
                _findMatchIndex = i;
                break;
            }
        }

        JumpToCurrentFindMatch();
    }

    private void StepFindMatch(int direction)
    {
        if (_findMatches.Count == 0)
        {
            return;
        }

        _findMatchIndex = ((_findMatchIndex + direction) % _findMatches.Count + _findMatches.Count) % _findMatches.Count;
        JumpToCurrentFindMatch();
    }

    private void JumpToCurrentFindMatch()
    {
        if (_findMatchIndex < 0 || _findMatchIndex >= _findMatches.Count)
        {
            return;
        }

        SelectRange(_findMatches[_findMatchIndex], _findBox.Text.Length);
        _findStatus.Text = $"{_findMatchIndex + 1}/{_findMatches.Count}";
    }

    /// <summary>Highlight-all-matches, clipped to one line at a time from <c>OnDraw</c>'s own loop.</summary>
    private void DrawFindMatchesForLine(CanvasDrawingSession ds, int lineStart, int lineEnd, float y)
    {
        if (_findMatches.Count == 0)
        {
            return;
        }

        var queryLength = _findBox.Text.Length;
        if (queryLength == 0)
        {
            return;
        }

        for (var i = 0; i < _findMatches.Count; i++)
        {
            var matchStart = _findMatches[i];
            var matchEnd = matchStart + queryLength;
            if (matchEnd <= lineStart || matchStart >= lineEnd)
            {
                continue;
            }

            var from = Math.Max(matchStart, lineStart) - lineStart;
            var to = Math.Min(matchEnd, lineEnd) - lineStart;
            var brush = i == _findMatchIndex ? _theme.FindMatchActiveFill : _theme.FindMatchFill;
            var x0 = ColumnX(from);
            var x1 = ColumnX(to);
            ds.FillRectangle(x0, y, Math.Max(2f, x1 - x0), _lineHeight, brush);
        }
    }
}
