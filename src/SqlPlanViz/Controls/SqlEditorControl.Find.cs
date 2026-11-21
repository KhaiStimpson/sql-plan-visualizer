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

    private readonly TextBox _replaceBox = new()
    {
        Width = 160,
        FontSize = 12,
        PlaceholderText = "Replace",
        IsSpellCheckEnabled = false,
    };

    private readonly Button _replaceOneButton = new() { Content = "Replace", FontSize = 11, Padding = new Thickness(8, 4, 8, 4), MinWidth = 0 };
    private readonly Button _replaceAllButton = new() { Content = "All", FontSize = 11, Padding = new Thickness(8, 4, 8, 4), MinWidth = 0 };

    private StackPanel _replaceRow = null!;
    private Border _findOverlay = null!;
    private IReadOnlyList<int> _findMatches = [];
    private int _findMatchIndex = -1;

    /// <summary>
    /// True while either box owns keyboard focus — guards the canvas's own key handlers (see
    /// <c>SqlEditorControl.Input.cs</c>) from acting on the document's selection while the user
    /// is typing a query or replacement instead.
    /// </summary>
    private bool FindOverlayHasFocus =>
        _findBox.FocusState != FocusState.Unfocused || _replaceBox.FocusState != FocusState.Unfocused;

    private Border BuildFindOverlay()
    {
        var findRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        findRow.Children.Add(_findBox);
        findRow.Children.Add(_findStatus);
        findRow.Children.Add(_findPreviousButton);
        findRow.Children.Add(_findNextButton);
        findRow.Children.Add(_findCloseButton);

        // Hidden for Ctrl+F, shown for Ctrl+H — same overlay, replace is just the find row's
        // sibling rather than a second dialog.
        _replaceRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Visibility = Visibility.Collapsed };
        _replaceRow.Children.Add(_replaceBox);
        _replaceRow.Children.Add(_replaceOneButton);
        _replaceRow.Children.Add(_replaceAllButton);

        var stack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4 };
        stack.Children.Add(findRow);
        stack.Children.Add(_replaceRow);

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
            Child = stack,
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

        _replaceBox.KeyDown += OnReplaceBoxKeyDown;
        _replaceOneButton.Click += (_, _) => ReplaceCurrentMatch();
        _replaceAllButton.Click += (_, _) => ReplaceAllMatches();

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
    private void OpenFind(bool withReplace = false)
    {
        if (HasSelection && SelectionLength is > 0 and < 200)
        {
            _findBox.Text = SelectedText;
        }

        _replaceRow.Visibility = withReplace && !IsReadOnly ? Visibility.Visible : Visibility.Collapsed;
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

    private void OnReplaceBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                CloseFind();
                e.Handled = true;
                break;

            case VirtualKey.Enter:
                ReplaceCurrentMatch();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Replaces the current match and re-runs the search, which keeps landing on the same
    /// document position — the next match, since the one just replaced no longer matches.
    /// One call to <see cref="ReplaceRange"/> is one undo step, so repeated replace-one presses
    /// undo individually rather than as a single block.
    /// </summary>
    private void ReplaceCurrentMatch()
    {
        if (IsReadOnly || _findMatchIndex < 0 || _findMatchIndex >= _findMatches.Count)
        {
            return;
        }

        var query = _findBox.Text;
        if (query.Length == 0)
        {
            return;
        }

        ReplaceRange(_findMatches[_findMatchIndex], query.Length, _replaceBox.Text);
        RunFindSearch();
    }

    /// <summary>Replaces every match, back to front so an earlier offset never shifts under it.</summary>
    private void ReplaceAllMatches()
    {
        if (IsReadOnly || _findMatches.Count == 0)
        {
            return;
        }

        var query = _findBox.Text;
        var replacement = _replaceBox.Text;
        for (var i = _findMatches.Count - 1; i >= 0; i--)
        {
            ReplaceRange(_findMatches[i], query.Length, replacement);
        }

        RunFindSearch();
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
    private void DrawFindMatchesForLine(CanvasDrawingSession ds, int line, int lineStart, int lineEnd, float y)
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
            var x0 = ColumnXExact(line, from);
            var x1 = ColumnXExact(line, to);
            ds.FillRectangle(x0, y, Math.Max(2f, x1 - x0), _lineHeight, brush);
        }
    }
}
