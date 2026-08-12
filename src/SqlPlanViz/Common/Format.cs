using System.Globalization;

namespace SqlPlanViz.Common;

/// <summary>Compact number formatting shared by the canvas and the detail panels.</summary>
public static class Format
{
    public static string Rows(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "–";
        }

        var abs = Math.Abs(value);
        return abs switch
        {
            >= 1_000_000_000 => (value / 1_000_000_000).ToString("0.##", CultureInfo.InvariantCulture) + "B",
            >= 1_000_000 => (value / 1_000_000).ToString("0.##", CultureInfo.InvariantCulture) + "M",
            >= 10_000 => (value / 1_000).ToString("0.#", CultureInfo.InvariantCulture) + "k",
            _ => value.ToString("#,##0.##", CultureInfo.CurrentCulture),
        };
    }

    public static string Cost(double value) =>
        value >= 1
            ? value.ToString("0.###", CultureInfo.CurrentCulture)
            : value.ToString("0.######", CultureInfo.CurrentCulture);

    public static string Milliseconds(double value) =>
        value >= 1000
            ? (value / 1000).ToString("0.##", CultureInfo.CurrentCulture) + " s"
            : value.ToString("#,##0.##", CultureInfo.CurrentCulture) + " ms";

    public static string Percent(double fraction) =>
        (fraction * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%";

    /// <summary>"12×" / "1,400×" — how far an estimate missed.</summary>
    public static string Factor(double factor) =>
        factor >= 100
            ? Rows(Math.Round(factor)) + "×"
            : factor.ToString("0.#", CultureInfo.CurrentCulture) + "×";
}
