using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SqlPlanViz.Diagnostics;
using Windows.UI;

namespace SqlPlanViz.Themes;

/// <summary>
/// Gauge brushes for x:Bind function bindings in data templates, where the brush depends on
/// data (a selected record, a severity) and so cannot be a fixed {ThemeResource}. Resolved
/// against <see cref="ThemeService.ActualTheme"/>; under a Windows contrast theme the
/// selected-record roles fall back to the system Highlight colours (COMPONENTS.md, Forced
/// colours). Templates re-evaluate when their item raises a change, which the page does on
/// theme change.
/// </summary>
public static class GaugeBrushes
{
    private static readonly Windows.UI.ViewManagement.AccessibilitySettings Accessibility = new();

    private static GaugeTokenSet Tokens => GaugeTokens.For(ThemeService.ActualTheme);

    private static bool HighContrast => Accessibility.HighContrast;

    private static SolidColorBrush Brush(Color color) => new(color);

    private static SolidColorBrush System(string key, Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Color c
            ? new SolidColorBrush(c)
            : new SolidColorBrush(fallback);

    /// <summary>A selected row's fill: sel, or transparent.</summary>
    public static Brush RowFill(bool selected) => !selected
        ? Brush(Colors.Transparent)
        : HighContrast ? System("SystemColorHighlightColor", Tokens.Sel) : Brush(Tokens.Sel);

    /// <summary>The 2px inset leading edge of a selected row: sel-edge.</summary>
    public static Brush RowEdge(bool selected) => !selected
        ? Brush(Colors.Transparent)
        : HighContrast ? System("SystemColorHighlightTextColor", Tokens.SelEdge) : Brush(Tokens.SelEdge);

    /// <summary>Titles and values: fg-0, or sel-fg on a selected row.</summary>
    public static Brush RowText(bool selected) => Text(selected, Tokens.Fg0);

    /// <summary>Body copy: fg-1, or sel-fg on a selected row.</summary>
    public static Brush RowBody(bool selected) => Text(selected, Tokens.Fg1);

    /// <summary>Metadata: fg-2, or sel-fg on a selected row.</summary>
    public static Brush RowMeta(bool selected) => Text(selected, Tokens.Fg2);

    /// <summary>An entity link's underline: fg-3, or sel-fg (currentColor) on a selected row.</summary>
    public static Brush RowUnderline(bool selected) => Text(selected, Tokens.Fg3);

    /// <summary>A severity word: crit / warn / fg-2, and sel-crit / sel-warn / sel-fg when selected.</summary>
    public static Brush Severity(FindingSeverity severity, bool selected)
    {
        if (selected)
        {
            return HighContrast
                ? System("SystemColorHighlightTextColor", Tokens.SelFg)
                : Brush(severity switch
                {
                    FindingSeverity.Critical => Tokens.SelCrit,
                    FindingSeverity.Warning => Tokens.SelWarn,
                    _ => Tokens.SelFg,
                });
        }

        return Brush(severity switch
        {
            FindingSeverity.Critical => Tokens.Crit,
            FindingSeverity.Warning => Tokens.Warn,
            _ => Tokens.Fg2,
        });
    }

    /// <summary>A Showplan warning's severity word: crit / warn / fg-2.</summary>
    public static Brush Warning(Model.WarningSeverity severity) => Brush(severity switch
    {
        Model.WarningSeverity.Critical => Tokens.Crit,
        Model.WarningSeverity.Warning => Tokens.Warn,
        _ => Tokens.Fg2,
    });

    private static Brush Text(bool selected, Color normal) => !selected
        ? Brush(normal)
        : HighContrast ? System("SystemColorHighlightTextColor", Tokens.SelFg) : Brush(Tokens.SelFg);
}
