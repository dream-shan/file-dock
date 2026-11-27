using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace FileDockDemo;

internal static class ThemePalette
{
    private static bool _installed;
    private static bool? _dark;
    internal static Func<bool>? SystemAppsDarkForVerification;
    private static readonly (string Key, string Light, string Dark)[] Colors = {
        // Layered Apple translucent glass fills. At the default 40% shell opacity,
        // cards compose to 66-70% opacity and inputs to 80%; text stays fully opaque.
        ("ContentSurface", "#80FFFFFF", "#6E2C2C2E"),
        ("PaneCard", "#80FFFFFF", "#722C2C2E"), ("PaneInput", "#AAFFFFFF", "#AA2C2C2E"),
        ("PaneOutline", "#22000000", "#24FFFFFF"),
        ("SpecularEdge", "#B8FFFFFF", "#30FFFFFF"),
        // The rail is a separate transparent window, without the shell underlay.
        ("TabSurface", "#B3F5F5F7", "#B32C2C2E"),
        ("TabSelected", "#CCFFFFFF", "#CC1C1C1E"), ("TabHover", "#CCEBF3FF", "#CC3A3A3C"),
        ("HeaderHover", "#14000000", "#1EFFFFFF"), ("HeaderPressed", "#24000000", "#30FFFFFF"),
        ("Surface", "#F5F5F7", "#1C1C1E"), ("Card", "#FFFFFF", "#2C2C2E"),
        ("Input", "#EBEBF0", "#3A3A3C"), ("Border", "#D2D2D7", "#38383A"),
        ("Ink", "#1D1D1F", "#F5F5F7"), ("Muted", "#636366", "#C7C7CC"),
        ("Accent", "#006EE6", "#0A84FF"), ("AccentSoft", "#1E006EE6", "#2E0A84FF"),
        ("Hover", "#12000000", "#1AFFFFFF"),
        ("Pressed", "#20000000", "#2AFFFFFF"), ("IconSurface", "#ECECF1", "#3A3A3C"),
        ("Badge", "#1A007AFF", "#2B0A84FF"), ("BadgeText", "#007AFF", "#64B5FF"),
        ("FolderFill", "#EBF5FF", "#260A84FF"), ("FolderStroke", "#007AFF", "#409CFF"),
        ("CpuAccent", "#007AFF", "#0A84FF"), ("GpuAccent", "#AF52DE", "#BF5AF2"),
        ("MemoryAccent", "#34C759", "#30D158"), ("VramAccent", "#FF9500", "#FF9F0A"),
        ("Track", "#22000000", "#2EFFFFFF"), ("SliderTrack", "#22000000", "#2BFFFFFF"),
        ("SliderFill", "#FFFFFF", "#F5F5F7"), ("SliderThumbBorder", "#26000000", "#1A000000"),
        ("SwitchOff", "#2E000000", "#38FFFFFF"), ("SwitchOn", "#34C759", "#30D158"),
        ("ToolPinAccent", "#AF52DE", "#BF5AF2"), ("ToolShotAccent", "#007AFF", "#0A84FF"),
        ("ToolKeyAccent", "#5856D6", "#5E5CE6"), ("OnAccent", "#FFFFFF", "#FFFFFF"),
        ("Error", "#FF3B30", "#FF453A"),
        ("Selection", "#24007AFF", "#380A84FF")
    };

    internal static string Normalize(string? mode) => mode is "Light" or "Dark" ? mode : "System";
    internal static bool Resolve(string? mode, bool systemDark) => Normalize(mode) switch { "Light" => false, "Dark" => true, _ => systemDark };
    internal static bool SystemAppsDark()
    {
        if (App.IsSelfTest && SystemAppsDarkForVerification != null) return SystemAppsDarkForVerification();
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { return false; }
    }
    internal static SolidColorBrush Brush(string key) => (SolidColorBrush)Application.Current.Resources[key];
    internal static void Apply(bool dark)
    {
        if (_dark != dark)
        {
            foreach (var item in Colors)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? item.Dark : item.Light));
                brush.Freeze(); Application.Current.Resources[item.Key] = brush;
            }
            Application.Current.Resources["HeaderHaloColor"] = dark
                ? Color.FromRgb(10, 10, 12) : System.Windows.Media.Colors.White;
            _dark = dark;
        }
        if (!_installed)
        {
            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ThemeResources.xaml", UriKind.Relative) });
            _installed = true;
        }
    }
}
