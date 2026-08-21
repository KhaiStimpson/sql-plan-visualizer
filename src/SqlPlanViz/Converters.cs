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

/// <summary>Bool to its negation — for an IsEnabled that has to be the opposite of a checkbox.</summary>
public sealed partial class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => value is not true;
}

/// <summary>Shows an element only when its bound string has something in it.</summary>
public sealed partial class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// Bridges <c>NumberBox.Value</c> (double) and <see cref="SqlPlanViz.ViewModels.ParameterBindingItem.Value"/>
/// (string), which stays a string all the way to <c>SqlLiteral.Format</c>. An unparsable or empty
/// string becomes <see cref="double.NaN"/>, which <c>NumberBox</c> renders as an empty box rather
/// than a forced 0.
/// </summary>
public sealed partial class StringToDoubleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string { Length: > 0 } text && double.TryParse(text, out var number) ? number : double.NaN;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is double number && !double.IsNaN(number) ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
}

/// <summary>
/// Bridges <c>ToggleSwitch.IsOn</c> (bool) and <see cref="SqlPlanViz.ViewModels.ParameterBindingItem.Value"/>
/// (string) for bit parameters, using the <c>"1"</c>/<c>"0"</c> spelling <c>SqlLiteral</c> already
/// accepts for bit literals.
/// </summary>
public sealed partial class StringToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string text && text == "1";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is true ? "1" : "0";
}

/// <summary>Tints odd-numbered parameter rows so a long strip reads by row rather than by eye-strain.</summary>
public sealed partial class RowIndexToAlternateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not int index || index % 2 == 0)
        {
            return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        return Application.Current.Resources.TryGetValue("LayerFillColorAltBrush", out var brush)
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
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
