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
        ("Surface", "#FAFBFD", "#202329"), ("Card", "#F1F4F9", "#2A2E36"),
        ("Input", "#F1F4F9", "#2A2E36"), ("Border", "#DCE3EE", "#3D4552"),
        ("Ink", "#273247", "#E5E8ED"), ("Muted", "#748196", "#A4AEBE"),
        ("Accent", "#4A6CF7", "#82B3FF"), ("Hover", "#E9EFFF", "#343F52"),
        ("Pressed", "#DEE6FD", "#3D4C65"), ("IconSurface", "#FFFFFF", "#343A45"),
        ("Badge", "#EDF1FF", "#333D50"), ("BadgeText", "#6478AD", "#B4C4E6"),
        ("FolderFill", "#FFF7E7", "#423927"), ("FolderStroke", "#D29524", "#E7B85B"),
        ("CpuAccent", "#4A6CF7", "#82B3FF"), ("GpuAccent", "#8B68D8", "#B99AEB"),
        ("MemoryAccent", "#239C88", "#65C8B9"), ("VramAccent", "#C38A3A", "#E1B96A"),
        ("Track", "#E0E7F2", "#414956"), ("Error", "#B34848", "#F29A9A"),
        ("Selection", "#D6E2FF", "#344969")
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
            _dark = dark;
        }
        if (!_installed)
        {
            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ThemeResources.xaml", UriKind.Relative) });
            _installed = true;
        }
    }
}
