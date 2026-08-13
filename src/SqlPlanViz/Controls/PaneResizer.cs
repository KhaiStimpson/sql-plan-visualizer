using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace SqlPlanViz.Controls;

/// <summary>
/// A drag handle for the edge of a pane. WinUI 3 has no GridSplitter, and the SQL pane needs
/// exactly one axis of it, so this is the whole feature: capture the pointer and report the
/// vertical distance dragged. The host decides what that means.
/// </summary>
public sealed class PaneResizer : ContentControl
{
    private uint _pointerId;
    private bool _dragging;
    private double _lastY;

    public PaneResizer()
    {
        IsTabStop = false;
        HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
        VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
    }

    /// <summary>Pixels dragged since the last report — positive is downwards.</summary>
    public event EventHandler<double>? Dragged;

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        _pointerId = e.Pointer.PointerId;
        _lastY = e.GetCurrentPoint(null).Position.Y;
        _dragging = CapturePointer(e.Pointer);
        e.Handled = _dragging;
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging || e.Pointer.PointerId != _pointerId)
        {
            return;
        }

        // Window coordinates: the handle itself moves as the pane resizes, so measuring
        // against it would feed the drag back into itself.
        var y = e.GetCurrentPoint(null).Position.Y;
        var delta = y - _lastY;
        _lastY = y;

        if (delta != 0)
        {
            Dragged?.Invoke(this, delta);
        }

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
        {
            ReleasePointerCapture(e.Pointer);
            _dragging = false;
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }
}
