using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace FileDockDemo;

public partial class MainWindow
{
    private bool _themeApplied, _themeRefreshQueued;
    internal bool IsDarkTheme { get; private set; }

    internal void ApplyTheme(bool? systemAppsDark = null)
    {
        if (_quitting) return;
        bool dark = ThemePalette.Resolve(State.ThemeMode, systemAppsDark ?? ThemePalette.SystemAppsDark());
        if (_themeApplied && IsDarkTheme == dark) return;
        IsDarkTheme = dark; _themeApplied = true;
        ThemePalette.Apply(dark);
        AppLogo.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/FileDock-" + (dark ? "light" : "dark") + ".png"));
        CpuCard.ApplyTheme(ThemePalette.Brush("CpuAccent"));
        GpuCard.ApplyTheme(ThemePalette.Brush("GpuAccent"));
        MemoryCard.ApplyTheme(ThemePalette.Brush("MemoryAccent"));
        VramCard.ApplyTheme(ThemePalette.Brush("VramAccent"));
        UpdateKeepOpen(); ApplyNativeTheme(); ApplyMemoryAppearance(false);
    }

    private void QueueThemeRefresh()
    {
        if (Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(QueueThemeRefresh); return; }
        if (_quitting || _themeRefreshQueued) return;
        _themeRefreshQueued = true;
        Dispatcher.BeginInvoke(() => { _themeRefreshQueued = false; if (_quitting) return; ApplyTheme(); UpdateTrayIcon(); }, DispatcherPriority.Background);
    }

    internal void SetThemeMode(string mode)
    {
        State.ThemeMode = ThemePalette.Normalize(mode);
        ApplyTheme(); Persist();
    }

    internal MenuItem CreateAppearanceMenu()
    {
        var appearance = new MenuItem { Header = "外观" };
        foreach (var option in new[] { ("System", "跟随系统"), ("Light", "浅色"), ("Dark", "深色") })
        {
            var item = new MenuItem { Header = option.Item2, Tag = option.Item1, IsCheckable = true, IsChecked = State.ThemeMode == option.Item1 };
            item.Click += (_, _) => {
                SetThemeMode(option.Item1);
                foreach (MenuItem other in appearance.Items) other.IsChecked = (string)other.Tag == State.ThemeMode;
            };
            appearance.Items.Add(item);
        }
        return appearance;
    }

    private void ApplyNativeTheme()
    {
        _dock?.ApplyTheme(IsDarkTheme);
        if (_tray?.ContextMenuStrip is Forms.ContextMenuStrip menu) ThemeMenus.Apply(menu, IsDarkTheme);
    }

    private void ShowThemedHelp(string text)
    {
        var panel = new DockPanel { Margin = new Thickness(20) };
        var close = new Button { Content = "关闭", IsCancel = true, Width = 84, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
        panel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 23 } });
        var dialog = new Window { Title = "文件坞 · 使用说明", Owner = this, Width = 460, Height = 530, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        dialog.SetResourceReference(BackgroundProperty, "Surface"); dialog.SetResourceReference(ForegroundProperty, "Ink");
        close.Click += (_, _) => dialog.Close(); dialog.ShowDialog();
    }
}

internal static class ThemeMenus
{
    internal static void Apply(Forms.ContextMenuStrip menu, bool dark)
    {
        var colors = new MenuColors(dark);
        menu.BackColor = colors.ToolStripDropDownBackground;
        menu.ForeColor = dark ? System.Drawing.Color.FromArgb(229,232,237) : System.Drawing.Color.FromArgb(39,50,71);
        menu.Renderer = new Forms.ToolStripProfessionalRenderer(colors) { RoundedEdges = false };
    }
    private sealed class MenuColors(bool dark) : Forms.ProfessionalColorTable
    {
        private System.Drawing.Color Surface => dark ? System.Drawing.Color.FromArgb(32,35,41) : System.Drawing.Color.FromArgb(250,251,253);
        private System.Drawing.Color Hover => dark ? System.Drawing.Color.FromArgb(52,63,82) : System.Drawing.Color.FromArgb(233,239,255);
        private System.Drawing.Color Border => dark ? System.Drawing.Color.FromArgb(61,69,82) : System.Drawing.Color.FromArgb(220,227,238);
        public override System.Drawing.Color ToolStripDropDownBackground => Surface;
        public override System.Drawing.Color ImageMarginGradientBegin => Surface;
        public override System.Drawing.Color ImageMarginGradientMiddle => Surface;
        public override System.Drawing.Color ImageMarginGradientEnd => Surface;
        public override System.Drawing.Color MenuItemSelected => Hover;
        public override System.Drawing.Color MenuItemBorder => Border;
        public override System.Drawing.Color MenuBorder => Border;
        public override System.Drawing.Color SeparatorDark => Border;
        public override System.Drawing.Color SeparatorLight => Surface;
    }
}
