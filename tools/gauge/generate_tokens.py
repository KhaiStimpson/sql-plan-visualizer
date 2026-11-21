"""Generates the app's Gauge token files from docs/design/gauge/tokens.json.

Outputs (both checked in, never hand-edited):
  src/SqlPlanViz/Themes/GaugeTheme.xaml  - Color + SolidColorBrush per token role, per theme,
                                           plus the Fluent resource overrides (literal colours,
                                           so each theme dictionary is self-contained).
  src/SqlPlanViz/Themes/GaugeTokens.cs   - the same values as static Colors for Win2D.

Run from the repo root:  python tools/gauge/generate_tokens.py
"""
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
TOKENS = json.loads((ROOT / "docs/design/gauge/tokens.json").read_text(encoding="utf-8"))
THEMES = TOKENS["themes"]
ROLES = list(THEMES["dark"].keys())


def pascal(role: str) -> str:
    return "".join(part[:1].upper() + part[1:] for part in role.split("-"))


def argb(hex_value: str) -> str:
    """tokens.json is #RRGGBB or #RRGGBBAA; XAML wants #AARRGGBB."""
    h = hex_value.lstrip("#")
    if len(h) == 6:
        h += "ff"
    return "#" + (h[6:8] + h[0:6]).upper()


def parts(hex_value: str):
    h = argb(hex_value).lstrip("#")
    return [int(h[i:i + 2], 16) for i in range(0, 8, 2)]


# Fluent resources that drive stock controls, mapped to a Gauge role. Each is emitted as a
# Color (key) and a brush (key + "Brush").
FLUENT = {
    # text
    "TextFillColorPrimary": "fg-0",
    "TextFillColorSecondary": "fg-2",
    "TextFillColorTertiary": "fg-3",
    "TextFillColorDisabled": "fg-3",
    "AccentTextFillColorPrimary": "accent",
    "AccentTextFillColorSecondary": "accent",
    "AccentTextFillColorTertiary": "accent",
    "TextOnAccentFillColorPrimary": "accent-ink",
    "TextOnAccentFillColorSecondary": "accent-ink",
    # control fills
    "ControlFillColorDefault": "bg-3",
    "ControlFillColorSecondary": "bg-4",
    "ControlFillColorTertiary": "bg-4",
    "ControlFillColorDisabled": "bg-2",
    "ControlFillColorInputActive": "bg-0",
    "ControlAltFillColorSecondary": "bg-2",
    "ControlAltFillColorTertiary": "bg-3",
    "ControlAltFillColorQuarternary": "bg-4",
    "ControlSolidFillColorDefault": "bg-3",
    "SubtleFillColorSecondary": "bg-3",
    "SubtleFillColorTertiary": "bg-4",
    # strokes
    "ControlStrokeColorDefault": "line-2",
    "ControlStrokeColorSecondary": "line-2",
    "ControlStrongStrokeColorDefault": "line-ctl",
    "ControlStrongStrokeColorDisabled": "line-2",
    "CardStrokeColorDefault": "line",
    "CardStrokeColorDefaultSolid": "line",
    "SurfaceStrokeColorDefault": "line-2",
    "SurfaceStrokeColorFlyout": "line-2",
    "DividerStrokeColorDefault": "line",
    "FocusStrokeColorOuter": "accent",
    "FocusStrokeColorInner": "bg-0",
    # accent fills
    "AccentFillColorDefault": "accent",
    "AccentFillColorSecondary": "accent",
    "AccentFillColorTertiary": "accent",
    "AccentFillColorDisabled": "bg-4",
    # surfaces
    "CardBackgroundFillColorDefault": "bg-1",
    "CardBackgroundFillColorSecondary": "bg-2",
    "LayerFillColorDefault": "bg-1",
    "LayerFillColorAlt": "bg-2",
    "LayerOnMicaBaseAltFillColorDefault": "bg-1",
    "SolidBackgroundFillColorBase": "bg-0",
    "SolidBackgroundFillColorBaseAlt": "bg-1",
    "SolidBackgroundFillColorSecondary": "bg-1",
    "SolidBackgroundFillColorTertiary": "bg-2",
    "SolidBackgroundFillColorQuarternary": "bg-3",
    "AcrylicBackgroundFillColorDefault": "bg-2",
    "AcrylicInAppFillColorDefault": "bg-2",
    "SmokeFillColorDefault": "scrim",
    # status
    "SystemFillColorCritical": "crit",
    "SystemFillColorCaution": "warn",
    "SystemFillColorSuccess": "ok",
    "SystemFillColorAttention": "info",
    "SystemFillColorNeutral": "fg-2",
    "SystemFillColorCriticalBackground": "crit-dim",
    "SystemFillColorCautionBackground": "warn-dim",
    "SystemFillColorSuccessBackground": "ok-dim",
    "SystemFillColorAttentionBackground": "info-dim",
    "SystemFillColorNeutralBackground": "bg-2",
}

# Control-level brushes (not "<color key>Brush" in Fluent), mapped straight to a role.
FLUENT_BRUSH_ONLY = {
    "FlyoutPresenterBackground": "bg-2",
    "FlyoutBorderThemeBrush": "line-2",
    "MenuFlyoutPresenterBackground": "bg-2",
    "MenuFlyoutPresenterBorderBrush": "line-2",
    "MenuFlyoutItemBackgroundPointerOver": "bg-4",
    "MenuFlyoutItemBackgroundPressed": "bg-4",
    "MenuFlyoutItemForeground": "fg-1",
    "MenuFlyoutItemForegroundPointerOver": "fg-0",
    "MenuFlyoutItemForegroundPressed": "fg-0",
    "ComboBoxDropDownBackground": "bg-2",
    "ComboBoxDropDownBorderBrush": "line-2",
    "ComboBoxItemBackgroundPointerOver": "bg-4",
    "ComboBoxItemBackgroundSelected": "sel",
    "ComboBoxItemBackgroundSelectedPointerOver": "sel",
    "ComboBoxItemForegroundSelected": "sel-fg",
    "ComboBoxItemPillFillBrush": "sel-edge",
    "ToolTipBackground": "bg-2",
    "ToolTipForeground": "fg-1",
    "ToolTipBorderBrush": "line-2",
    "ContentDialogBackground": "bg-2",
    "ContentDialogBorderBrush": "line-2",
    "ContentDialogTopOverlay": "bg-2",
    "ContentDialogForeground": "fg-1",
    "ContentDialogSmokeFill": "scrim",
    "ScrollBarThumbFill": "line-ctl",
    "ScrollBarThumbFillPointerOver": "fg-3",
    "ScrollBarThumbFillPressed": "fg-2",
    "ScrollBarPanningThumbBackground": "line-ctl",
    "ApplicationPageBackgroundThemeBrush": "bg-0",
    "TextControlForeground": "fg-0",
    "TextControlForegroundPointerOver": "fg-0",
    "TextControlForegroundFocused": "fg-0",
    "TextControlPlaceholderForeground": "fg-3",
    "TextControlPlaceholderForegroundPointerOver": "fg-3",
    "TextControlPlaceholderForegroundFocused": "fg-3",
    "TextControlBackground": "bg-0",
    "TextControlBackgroundPointerOver": "bg-0",
    "TextControlBackgroundFocused": "bg-0",
    "TextControlBorderBrush": "line-ctl",
    "TextControlBorderBrushPointerOver": "fg-3",
    "TextControlBorderBrushFocused": "accent",
    "TextControlSelectionHighlightColor": "sel",
    # Focus: a 2px accent ring app-wide (FrameworkElement.FocusVisualPrimaryBrush defaults here).
    "SystemControlFocusVisualPrimaryBrush": "accent",
    "SystemControlFocusVisualSecondaryBrush": "bg-0",
    "ControlElevationBorderBrush": "line-2",
    "AccentControlElevationBorderBrush": "accent",
    "TextControlElevationBorderBrush": "line-ctl",
    "TextControlElevationBorderFocusedBrush": "accent",
    "ComboBoxBackground": "bg-0",
    "ComboBoxBackgroundPointerOver": "bg-0",
    "ComboBoxBackgroundPressed": "bg-0",
    "ComboBoxBackgroundFocused": "bg-0",
    "ComboBoxBackgroundUnfocused": "bg-0",
    "ComboBoxBorderBrush": "line-ctl",
    "ComboBoxBorderBrushPointerOver": "fg-3",
    "ComboBoxBorderBrushPressed": "accent",
    "ComboBoxBorderBrushFocused": "accent",
    "ComboBoxForeground": "fg-0",
    "ComboBoxForegroundFocused": "fg-0",
    "ComboBoxPlaceHolderForeground": "fg-3",
    "ComboBoxDropDownGlyphForeground": "fg-1",
    "CheckBoxCheckBackgroundFillUnchecked": "bg-0",
    "CheckBoxCheckBackgroundFillUncheckedPointerOver": "bg-0",
    "CheckBoxCheckBackgroundStrokeUnchecked": "line-ctl",
    "CheckBoxCheckBackgroundStrokeUncheckedPointerOver": "fg-3",
    "CheckBoxCheckBackgroundFillChecked": "accent",
    "CheckBoxCheckBackgroundFillCheckedPointerOver": "accent",
    "CheckBoxCheckBackgroundStrokeChecked": "accent",
    "CheckBoxCheckGlyphForegroundChecked": "accent-ink",
    "CheckBoxCheckGlyphForegroundCheckedPointerOver": "accent-ink",
    "CheckBoxForegroundUnchecked": "fg-1",
    "CheckBoxForegroundChecked": "fg-1",
    "ListViewItemBackgroundSelected": "sel",
    "ListViewItemBackgroundSelectedPointerOver": "sel",
    "ListViewItemBackgroundPointerOver": "bg-2",
    "ListViewItemForegroundSelected": "sel-fg",
    "InfoBarErrorSeverityBackgroundBrush": "bg-2",
    "InfoBarWarningSeverityBackgroundBrush": "bg-2",
    "InfoBarInformationalSeverityBackgroundBrush": "bg-2",
    "InfoBarSuccessSeverityBackgroundBrush": "bg-2",
    "InfoBarBorderBrush": "line-2",
    "InfoBarErrorSeverityIconBackground": "crit",
    "InfoBarWarningSeverityIconBackground": "warn",
    "InfoBarSuccessSeverityIconBackground": "ok",
    "InfoBarInformationalSeverityIconBackground": "info",
}

# Accent ramp. WinUI derives the accent fills from these, so pin every step to the accent role.
ACCENT_RAMP = ["SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2",
               "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2",
               "SystemAccentColorDark3"]

# High contrast: the system palette replaces Gauge. Every role maps to a system colour, so
# selection, current nav, pressed segments and borders stay visible (COMPONENTS.md, Forced colours).
HC = {
    "bg-0": "SystemColorWindowColor", "bg-1": "SystemColorWindowColor",
    "bg-2": "SystemColorWindowColor", "bg-3": "SystemColorButtonFaceColor",
    "bg-4": "SystemColorHighlightColor",
    "line": "SystemColorWindowTextColor", "line-2": "SystemColorWindowTextColor",
    "line-ctl": "SystemColorButtonTextColor",
    "fg-0": "SystemColorWindowTextColor", "fg-1": "SystemColorWindowTextColor",
    "fg-2": "SystemColorWindowTextColor", "fg-3": "SystemColorGrayTextColor",
    "accent": "SystemColorHighlightColor", "accent-dim": "SystemColorWindowColor",
    "accent-ink": "SystemColorHighlightTextColor",
    "sel": "SystemColorHighlightColor", "sel-fg": "SystemColorHighlightTextColor",
    "sel-crit": "SystemColorHighlightTextColor", "sel-warn": "SystemColorHighlightTextColor",
    "sel-edge": "SystemColorHighlightTextColor",
    "crit": "SystemColorWindowTextColor", "crit-dim": "SystemColorWindowColor",
    "warn": "SystemColorWindowTextColor", "warn-dim": "SystemColorWindowColor",
    "info": "SystemColorWindowTextColor", "info-dim": "SystemColorWindowColor",
    "ok": "SystemColorWindowTextColor", "ok-dim": "SystemColorWindowColor",
    "code": "SystemColorWindowTextColor", "val-str": "SystemColorWindowTextColor",
    "val-num": "SystemColorWindowTextColor", "tm-fg": "SystemColorWindowTextColor",
    "shadow": "SystemColorWindowColor", "scrim": "SystemColorWindowColor",
}
for i in range(1, 10):
    HC[f"tm-{i}"] = "SystemColorWindowColor"
assert set(HC) == set(ROLES), set(ROLES) ^ set(HC)

# In high contrast, leave Fluent's own HC resources alone: only the Gauge roles are defined.
INCLUDE_FLUENT = True


def theme_dict(key: str, theme: str | None) -> str:
    out = [f'        <ResourceDictionary x:Key="{key}">']
    if theme is None:
        def color(role):
            return "{ThemeResource " + HC[role] + "}"
    else:
        def color(role):
            return argb(THEMES[theme][role])
    out.append("            <!--  Gauge roles  -->")
    for role in ROLES:
        out.append(f'            <Color x:Key="Gauge{pascal(role)}">{color(role)}</Color>')
    for role in ROLES:
        out.append(f'            <SolidColorBrush x:Key="Gauge{pascal(role)}Brush" Color="{color(role)}" />')
    if theme is not None and INCLUDE_FLUENT:
        out.append("")
        out.append("            <!--  Fluent overrides: stock controls read as Gauge  -->")
        for key_, role in FLUENT.items():
            out.append(f'            <Color x:Key="{key_}">{color(role)}</Color>')
            out.append(f'            <SolidColorBrush x:Key="{key_}Brush" Color="{color(role)}" />')
        for key_, role in FLUENT_BRUSH_ONLY.items():
            out.append(f'            <SolidColorBrush x:Key="{key_}" Color="{color(role)}" />')
        for key_ in ACCENT_RAMP:
            out.append(f'            <Color x:Key="{key_}">{color("accent")}</Color>')
    out.append("        </ResourceDictionary>")
    return "\n".join(out)


def radius(name: str) -> str:
    return TOKENS["shared"][name].replace("px", "")


XAML = f'''<?xml version="1.0" encoding="utf-8"?>
<!--
    GENERATED by tools/gauge/generate_tokens.py from docs/design/gauge/tokens.json.
    Do not edit by hand: change tokens.json (or the mapping tables in the script) and re-run.
    Themes/GaugeTokens.cs is generated from the same source for Win2D, so the two always match.
-->
<ResourceDictionary
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <ResourceDictionary.ThemeDictionaries>
{theme_dict("Default", "dark")}

{theme_dict("Light", "light")}

{theme_dict("HighContrast", None)}
    </ResourceDictionary.ThemeDictionaries>

    <!--  Shared: type and radii  -->
    <FontFamily x:Key="GaugeSans">Segoe UI Variable Text, Segoe UI Variable, Segoe UI</FontFamily>
    <FontFamily x:Key="GaugeMono">Cascadia Code, Cascadia Mono, Consolas</FontFamily>
    <x:Double x:Key="GaugeFontSize">13</x:Double>
    <x:Double x:Key="GaugeMonoFontSize">12</x:Double>
    <CornerRadius x:Key="GaugeR1">{radius("r-1")}</CornerRadius>
    <CornerRadius x:Key="GaugeR2">{radius("r-2")}</CornerRadius>
    <CornerRadius x:Key="GaugeR3">{radius("r-3")}</CornerRadius>
    <CornerRadius x:Key="GaugeRDialog">6</CornerRadius>
</ResourceDictionary>
'''


def cs_theme(theme: str) -> str:
    lines = []
    for role in ROLES:
        a, r, g, b = parts(THEMES[theme][role])
        lines.append(f"        {pascal(role)} = Color.FromArgb(0x{a:02X}, 0x{r:02X}, 0x{g:02X}, 0x{b:02X}),")
    return "\n".join(lines)


PROPS = "\n".join(f"    public required Color {pascal(r)} {{ get; init; }}" for r in ROLES)

CS = f'''// <auto-generated>
// GENERATED by tools/gauge/generate_tokens.py. Mirrors docs/design/gauge/tokens.json, the
// same source as Themes/GaugeTheme.xaml, so Win2D drawing and XAML always use identical values.
// Do not edit by hand: change tokens.json and re-run the script.
// </auto-generated>
using Windows.UI;

namespace SqlPlanViz.Themes;

/// <summary>One theme's Gauge colours, for code that cannot read XAML resources (Win2D).</summary>
public sealed class GaugeTokenSet
{{
{PROPS}
}}

public static class GaugeTokens
{{
    public static readonly GaugeTokenSet Dark = new()
    {{
{cs_theme("dark")}
    }};

    public static readonly GaugeTokenSet Light = new()
    {{
{cs_theme("light")}
    }};

    /// <summary>The token set for a resolved (non-Default) theme.</summary>
    public static GaugeTokenSet For(Microsoft.UI.Xaml.ElementTheme theme) =>
        theme == Microsoft.UI.Xaml.ElementTheme.Light ? Light : Dark;
}}
'''

(ROOT / "src/SqlPlanViz/Themes/GaugeTheme.xaml").write_text(XAML, encoding="utf-8")
(ROOT / "src/SqlPlanViz/Themes/GaugeTokens.cs").write_text(CS, encoding="utf-8")
print("wrote GaugeTheme.xaml and GaugeTokens.cs:", len(ROLES), "roles")
