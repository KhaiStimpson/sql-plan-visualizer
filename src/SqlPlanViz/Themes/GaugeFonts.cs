using Microsoft.UI.Xaml.Media;

namespace SqlPlanViz.Themes;

/// <summary>Gauge's type families for code-built UI; XAML uses the GaugeSans / GaugeMono resources.</summary>
public static class GaugeFonts
{
    public const string SansFamily = "Segoe UI Variable Text, Segoe UI Variable, Segoe UI";

    public const string MonoFamily = "Cascadia Code, Cascadia Mono, Consolas";

    /// <summary>Win2D / DirectWrite takes one family name; the system falls back from there.</summary>
    public const string MonoSingle = "Cascadia Code";

    public const string SansSingle = "Segoe UI Variable Text";

    public static FontFamily Sans { get; } = new(SansFamily);

    public static FontFamily Mono { get; } = new(MonoFamily);
}
