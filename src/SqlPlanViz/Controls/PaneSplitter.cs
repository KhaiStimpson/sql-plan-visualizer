using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace SqlPlanViz.Controls;

/// <summary>
/// A horizontal drag handle that resizes the grid row below it
/// (live-plan-editor-plan.md Phase 5: "a splitter replaces the fixed 190px strip").
///
/// Written rather than imported: WinUI 3 has no built-in GridSplitter, and the Community
/// Toolkit's Sizers package is a whole dependency for one draggable bar. Keyboard resizing is
/// included because a splitter that only responds to the mouse makes the pane it controls
/// unreachable for anyone not using one.
/// </summary>
public sealed partial class PaneSplitter : Control
{
    private const double DefaultStep = 24;

    private bool _dragging;
    private double _dragStartY;
    private double _dragStartHeight;

    public PaneSplitter()
    {
        Height = 6;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        PointerEntered += (_, _) =>
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
            VisualStateManager.GoToState(this, "PointerOver", true);
        };
        PointerExited += (_, _) => VisualStateManager.GoToState(this, _dragging ? "Pressed" : "Normal", true);
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        KeyDown += OnKeyDown;

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, "Resize the SQL editor pane");
    }

    /// <summary>The row whose height this splitter changes. Set by the host after layout.</summary>
    public RowDefinition? TargetRow { get; set; }

    public double MinimumHeight { get; set; } = 0;

    public double MaximumHeight { get; set; } = 900;

    /// <summary>Raised after every resize, so the host can persist the height or update a toggle.</summary>
    public event EventHandler<double>? Resized;

    public void SetHeight(double height)
    {
        if (TargetRow is null)
        {
            return;
        }

        var clamped = Math.Clamp(height, MinimumHeight, MaximumHeight);
        TargetRow.Height = new GridLength(clamped);
        Resized?.Invoke(this, clamped);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (TargetRow is null)
        {
            return;
        }

        _dragging = true;
        _dragStartY = e.GetCurrentPoint(null).Position.Y;
        _dragStartHeight = TargetRow.ActualHeight;
        CapturePointer(e.Pointer);
        Focus(FocusState.Pointer);
        VisualStateManager.GoToState(this, "Pressed", true);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        // Dragging up grows the pane, so the delta is subtracted rather than added.
        var delta = _dragStartY - e.GetCurrentPoint(null).Position.Y;
        SetHeight(_dragStartHeight + delta);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleasePointerCapture(e.Pointer);
        VisualStateManager.GoToState(this, "PointerOver", true);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (TargetRow is null)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Up:
                SetHeight(TargetRow.ActualHeight + DefaultStep);
                break;

            case VirtualKey.Down:
                SetHeight(TargetRow.ActualHeight - DefaultStep);
                break;

            default:
                return;
        }

        e.Handled = true;
    }
}
