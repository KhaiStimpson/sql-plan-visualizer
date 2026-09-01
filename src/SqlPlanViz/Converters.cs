using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using SqlPlanViz.Diagnostics;
using SqlPlanViz.Model;

namespace SqlPlanViz;

public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}

public sealed partial class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is not Visibility.Visible;
}

/// <summary>Colours a warning's icon by severity, using the Fluent system palette.</summary>
public sealed partial class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value switch
        {
            WarningSeverity.Critical => "SystemFillColorCriticalBrush",
            WarningSeverity.Warning => "SystemFillColorCautionBrush",
            FindingSeverity.Critical => "SystemFillColorCriticalBrush",
            FindingSeverity.Warning => "SystemFillColorCautionBrush",
            _ => "SystemFillColorNeutralBrush",
        };

        return Application.Current.Resources.TryGetValue(key, out var brush)
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Highlights a cost-model divergence row that crosses the flagging threshold (hot-path-plan.md Phase 2).</summary>
public sealed partial class DivergenceForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is true ? "SystemFillColorCautionBrush" : "TextFillColorTertiaryBrush";
        return Application.Current.Resources.TryGetValue(key, out var brush)
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
