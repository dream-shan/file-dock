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
        // These are background fills only. At the default 40% shell opacity,
        // cards compose to 64-70% opacity and inputs to 80%; text stays opaque.
        ("ContentSurface", "#80FAFBFD", "#6620252D"),
        ("PaneCard", "#80F0F3F8", "#662A313C"), ("PaneInput", "#AAF0F3F8", "#AA2A313C"),
        ("PaneOutline", "#3056657B", "#30AFBACB"),
        ("Surface", "#FAFBFD", "#20252D"), ("Card", "#F0F3F8", "#2A313C"),
        ("Input", "#F0F3F8", "#2A313C"), ("Border", "#D6DFEB", "#414C5D"),
        ("Ink", "#253247", "#F0F3F8"), ("Muted", "#56657B", "#AFBACB"),
        ("Accent", "#345FCC", "#91BBFF"), ("Hover", "#E8EFFC", "#354358"),
        ("Pressed", "#DCE7FB", "#405573"), ("IconSurface", "#E8EDF5", "#343E4D"),
        ("Badge", "#EDF1FF", "#333D50"), ("BadgeText", "#6478AD", "#B4C4E6"),
        ("FolderFill", "#FFF7E7", "#423B2D"), ("FolderStroke", "#956719", "#E5BB68"),
        ("CpuAccent", "#345FCC", "#91BBFF"), ("GpuAccent", "#7954BD", "#BEA5ED"),
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
