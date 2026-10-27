using System;
using System.Drawing;
using System.Windows;
using Microsoft.Win32;
using DrawingSize = System.Drawing.Size;

namespace FileDockDemo;

public partial class MainWindow
{
    internal static Icon LoadMarkIcon(bool light, DrawingSize size)
    {
        var uri = new Uri("pack://application:,,,/Assets/FileDock-" + (light ? "light" : "dark") + ".ico");
        using var stream = Application.GetResourceStream(uri)!.Stream;
        using var source = new Icon(stream, size);
        return (Icon)source.Clone();
    }

    private static Icon CreateTrayIcon()
    {
        bool light = true;
        try { light = (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) == 0; }
        catch (System.Security.SecurityException) { }
        return LoadMarkIcon(light, System.Windows.Forms.SystemInformation.SmallIconSize);
    }

    private void TrayPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        QueueThemeRefresh();
    }

    private void UpdateTrayIcon()
    {
        if (_quitting || _tray == null) return;
        var replacement = CreateTrayIcon();
        var previous = _trayIcon;
        _tray.Icon = replacement;
        _trayIcon = replacement;
        previous?.Dispose();
    }
}
