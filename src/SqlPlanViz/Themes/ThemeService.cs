using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace SqlPlanViz.Themes;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>
/// The user's System / Light / Dark choice, applied to the root element's
/// <see cref="FrameworkElement.RequestedTheme"/> before the window is first shown (so there is
/// no flash of the system theme), and to the caption buttons per Gauge PLATFORMS.md.
/// </summary>
/// <remarks>
/// Persisted as JSON under the per-user local app-data folder rather than
/// <c>ApplicationData.Current.LocalSettings</c>: the app is unpackaged, where that API throws.
/// Same rationale as <see cref="Capture.ConnectionProfileStore"/>.
/// </remarks>
public static class ThemeService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SqlPlanViz",
        "ui-settings.json");

    private static FrameworkElement? _root;
    private static Window? _window;
    private static bool _loaded;
    private static ThemePreference _preference;

    /// <summary>Raised after the resolved theme changes, whether by the user or the system.</summary>
    public static event EventHandler? ThemeChanged;

    public static ThemePreference Preference
    {
        get
        {
            EnsureLoaded();
            return _preference;
        }
    }

    /// <summary>The theme actually in effect (never Default).</summary>
    public static ElementTheme ActualTheme =>
        _root?.ActualTheme ?? (Application.Current.RequestedTheme == ApplicationTheme.Light
            ? ElementTheme.Light
            : ElementTheme.Dark);

    /// <summary>Applies the stored preference to <paramref name="root"/> and colours the window chrome.</summary>
    public static void Attach(Window window, FrameworkElement root)
    {
        EnsureLoaded();
        _window = window;
        _root = root;
        root.RequestedTheme = ToElementTheme(_preference);
        root.ActualThemeChanged += (_, _) => OnActualThemeChanged();
        window.Activated += (_, _) => ApplyCaptionColors();
        ApplyCaptionColors();
    }

    public static void SetPreference(ThemePreference preference)
    {
        EnsureLoaded();
        if (preference == _preference)
        {
            return;
        }

        _preference = preference;
        Save();

        if (_root is not null)
        {
            _root.RequestedTheme = ToElementTheme(preference);
        }
    }

    private static void OnActualThemeChanged()
    {
        ApplyCaptionColors();
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static ElementTheme ToElementTheme(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => ElementTheme.Light,
        ThemePreference.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>Caption buttons per PLATFORMS.md: fg-2 glyph (fg-3 inactive), bg-3/bg-4 hover and press.</summary>
    private static void ApplyCaptionColors()
    {
        if (_window is null || !AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var t = GaugeTokens.For(ActualTheme);
        var bar = _window.AppWindow.TitleBar;
        bar.BackgroundColor = t.Bg2;
        bar.InactiveBackgroundColor = t.Bg2;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = t.Fg2;
        bar.ButtonInactiveForegroundColor = t.Fg3;
        bar.ButtonHoverBackgroundColor = t.Bg3;
        bar.ButtonHoverForegroundColor = t.Fg0;
        bar.ButtonPressedBackgroundColor = t.Bg4;
        bar.ButtonPressedForegroundColor = t.Fg0;
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath));
                if (Enum.TryParse<ThemePreference>(settings?.Theme, ignoreCase: true, out var parsed))
                {
                    _preference = parsed;
                }
            }
        }
        catch (Exception)
        {
            // A corrupt settings file falls back to following the system.
            _preference = ThemePreference.System;
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new UiSettings { Theme = _preference.ToString() }));
        }
        catch (Exception)
        {
            // Not being able to remember the theme is not worth interrupting the user for.
        }
    }

    private sealed class UiSettings
    {
        public string? Theme { get; set; }
    }
}
