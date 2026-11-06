using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FileDockDemo;

internal static class ThemeVerification
{
    private static void VerifyHeaderContrast(MainWindow window,string root,Action<bool,string> check)
    {
        string mode=window.State.ThemeMode;int transparency=window.State.EmptyAreaTransparency;
        static Color Blend(Color top,Color bottom){double a=top.A/255d;return Color.FromRgb((byte)Math.Round(top.R*a+bottom.R*(1-a)),(byte)Math.Round(top.G*a+bottom.G*(1-a)),(byte)Math.Round(top.B*a+bottom.B*(1-a)));}
        static double Contrast(Color a,Color b){static double L(Color c){static double C(byte v){double x=v/255d;return x<=.04045?x/12.92:Math.Pow((x+.055)/1.055,2.4);}return .2126*C(c.R)+.7152*C(c.G)+.0722*C(c.B);}double x=L(a),y=L(b);return(Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);}
        try
        {
            foreach(string theme in new[]{"Dark","Light"})
            {
                window.SetThemeMode(theme);window.SetEmptyAreaTransparency(85,false);window.UpdateLayout();
                foreach(var backdrop in new[]{(Name:"white",Color:Colors.White),(Name:"black",Color:Colors.Black)})
                {
                    var shell=Blend(((SolidColorBrush)window.PanelSurface.Background).Color,backdrop.Color);
                    var buttons=new[]{window.KeepOpenButton,window.SettingsButton,window.CollapseButton};
                    foreach (var button in buttons)
                    {
                        var originalForeground = button.Foreground;
                        foreach (var foreground in new[] { originalForeground, ThemePalette.Brush("Accent") })
                        {
                            button.SetCurrentValue(Control.ForegroundProperty, foreground);
                            var iconVisual = new DrawingVisual();
                            using (var drawing = iconVisual.RenderOpen())
                            {
                                drawing.DrawRectangle(new SolidColorBrush(shell), null, new Rect(0, 0, 32, 32));
                                drawing.DrawRectangle(new VisualBrush(button), null, new Rect(0, 0, 32, 32));
                            }
                            var icon = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
                            icon.Render(iconVisual);
                            var pixels = new byte[32 * 32 * 4]; icon.CopyPixels(pixels, 32 * 4, 0);
                            double strongestEdge = 1;
                            for (int y = 5; y < 27; y++)
                                for (int x = 5; x < 27; x++)
                                {
                                    int offset = (y * 32 + x) * 4;
                                    var center = Color.FromRgb(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
                                    // A thin two-tone icon must contrast with its immediate surroundings.
                                    foreach (int delta in new[] { 2, 64 })
                                    {
                                        int neighbor = offset + delta * 4;
                                        var outside = Color.FromRgb(pixels[neighbor + 2], pixels[neighbor + 1], pixels[neighbor]);
                                        strongestEdge = Math.Max(strongestEdge, Contrast(center, outside));
                                    }
                                }
                            check(strongestEdge >= 3, $"{button.Name} rendered glyph and halo have a visible 3:1 edge over {backdrop.Name} in {theme}");
                        }
                        button.SetCurrentValue(Control.ForegroundProperty, originalForeground);
                    }
                    var header=new RenderTargetBitmap((int)window.ActualWidth,64,96,96,PixelFormats.Pbgra32);header.Render(window);
                    var visual=new DrawingVisual();using(var drawing=visual.RenderOpen()){drawing.DrawRectangle(new SolidColorBrush(backdrop.Color),null,new Rect(0,0,header.PixelWidth,64));drawing.DrawImage(header,new Rect(0,0,header.PixelWidth,64));}
                    var preview=new RenderTargetBitmap(header.PixelWidth,64,96,96,PixelFormats.Pbgra32);preview.Render(visual);
                    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(preview));using var file=File.Create(Path.Combine(root,$"header-{theme}-{backdrop.Name}.png"));encoder.Save(file);
                }
            }
        }
        finally{window.SetEmptyAreaTransparency(transparency,false);window.SetThemeMode(mode);}
    }
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    internal static async Task Run(MainWindow window, string root, Action<bool,string> check)
    {
        string original = window.State.ThemeMode;
        bool systemDark = false;
        ThemePalette.SystemAppsDarkForVerification = () => systemDark;
        try
        {
            check(JsonSerializer.Deserialize<AppState>("{\"Pins\":[]}")!.ThemeMode == "System" && ThemePalette.Normalize("unknown") == "System", "Old and unknown theme settings safely default to system without losing pins");
            await TransparencyVerification.Run(window,root,check);
            VerifyHeaderContrast(window,root,check);
            window.GoHome(); window.ExpandPanel(); window.SetThemeMode("System");
            check(!window.IsDarkTheme && ((SolidColorBrush)window.PanelSurface.Background).Color == Color.FromArgb(window.BlankAlpha,250,251,253), "System light application theme applies to the panel");
            window.CpuCard.Set(17, "theme validation", 60, true); int history = window.CpuCard.HistoryCount;
            window.SetThemeMode("Dark");
            check(window.IsDarkTheme && window.CpuCard.ValueText == "17%" && window.CpuCard.HistoryCount == history, "Changing theme preserves current performance values and history");
            check(((SolidColorBrush)window.PanelSurface.Background).Color == Color.FromArgb(window.BlankAlpha,32,37,45) && window.SearchBox.Foreground == ThemePalette.Brush("Ink") && window.CpuCard.Background == ThemePalette.Brush("PaneCard"), "Dark theme reaches panel, search and performance cards");
            check(((BitmapImage)window.AppLogo.Source).UriSource.ToString().Contains("light.png"), "Dark panel uses the light transparent mark");
            await window.WaitForSavesAsync();check(new StorageService(Path.Combine(root,"data")).Load().ThemeMode == "Dark", "Manual appearance choice persists to the existing settings file");
            var appearance = window.CreateAppearanceMenu();
            var options = appearance.Items.Cast<MenuItem>().ToArray();
            check(options.Length == 3 && options.Single(i => i.IsChecked).Tag.ToString() == "Dark", "Appearance menu exposes system, light and dark with one active choice");
            options.Single(i => (string)i.Tag == "Light").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check(!window.IsDarkTheme && window.State.ThemeMode == "Light" && options.Count(i => i.IsChecked) == 1, "Appearance menu changes mode and maintains exclusive check marks");
            systemDark = true;
            PostMessage(new WindowInteropHelper(window).Handle, 0x031A, IntPtr.Zero, IntPtr.Zero);
            await Task.Delay(180);
            check(!window.IsDarkTheme, "Manual light mode ignores a system dark-theme notification");
            window.SetThemeMode("System");
            check(window.IsDarkTheme, "Returning to system mode immediately follows the current application theme");
            systemDark = false;
            check(PostMessage(new WindowInteropHelper(window).Handle, 0x031A, IntPtr.Zero, IntPtr.Zero), "System theme notification posts to the live window handle");
            for(int i=0;i<30 && window.IsDarkTheme;i++) await Task.Delay(50);
            check(!window.IsDarkTheme, "Real window theme-change messages update system mode without a restart or polling");
            window.DockEntry.SetMemoryPressure(null,false);
            check(window.DockEntry.BackColor == System.Drawing.Color.White && window.DockEntry.Width == 20 && window.DockEntry.Opacity == .38, "Light edge entry keeps its compact size and idle opacity");
            await Task.Delay(1200);
            DemoVerification.SaveRender(window, Path.Combine(root,"theme-home-light.png"));
            window.SetThemeMode("Dark");
            window.SettingsButton.Focus();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var focusBorder = (Border)window.SettingsButton.Template.FindName("ButtonBorder",window.SettingsButton);
            check(window.SettingsButton.IsKeyboardFocused && focusBorder.Background is SolidColorBrush { Color.A: 0 }, "Focus alone keeps the header button background clear after a menu closes");
            check(window.SettingsButton.FocusVisualStyle != null && window.KeepOpenButton.FocusVisualStyle == window.SettingsButton.FocusVisualStyle,
                "Header buttons share a keyboard-navigation focus visual without changing their background");
            DemoVerification.SaveRender(window, Path.Combine(root,"theme-home-dark.png"));
            window.DockEntry.SetMemoryPressure(null,false);
            check(window.DockEntry.BackColor == System.Drawing.Color.FromArgb(48,53,62), "Dark edge entry receives the matching native background");
            using(var native = new System.Windows.Forms.ContextMenuStrip())
            {
                ThemeMenus.Apply(native,true);
                check(native.BackColor == System.Drawing.Color.FromArgb(32,35,41) && native.ForeColor.R > 200, "Native tray and edge menus receive contrasted dark colors");
            }
            await window.NavigateToFolderAsync(Path.Combine(root,"中文与空格 files"));
            window.FolderFilterComboBox.IsDropDownOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(window.FolderFilterComboBox.Background == ThemePalette.Brush("Card") && window.FolderFilterComboBox.IsDropDownOpen, "Folder type dropdown opens with dark colors");
            window.FolderFilterComboBox.IsDropDownOpen = false;
            DemoVerification.SaveRender(window, Path.Combine(root,"theme-folder-dark.png"));
            window.Width=380; window.Height=480; window.UpdateLayout();
            check(window.FolderList.ActualHeight > 80, "Themed browser remains usable at the minimum window size");
            window.Width=430; window.Height=650;
            var menu = new ContextMenu { PlacementTarget=window.SettingsButton }; menu.Items.Add(window.CreateAppearanceMenu()); menu.Items.Add(new MenuItem { Header="主题检查" });
            menu.IsOpen=true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(menu.Background == ThemePalette.Brush("Surface") && menu.Foreground == ThemePalette.Brush("Ink"), "WPF menus use theme colors outside the main visual tree");
            var submenu=(MenuItem)menu.Items[0]; submenu.IsSubmenuOpen=true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(submenu.IsSubmenuOpen && submenu.Items.Count == 3, "Themed submenu opens normally");
            submenu.IsSubmenuOpen=false; menu.IsOpen=false;
            var tip = new ToolTip { Content="图片预览", PlacementTarget=window.FolderList, IsOpen=true };
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(tip.Background == ThemePalette.Brush("Surface") && tip.Foreground == ThemePalette.Brush("Ink"), "Preview tooltip inherits dark foreground and background");
            tip.IsOpen=false;
        }
        finally
        {
            ThemePalette.SystemAppsDarkForVerification=null;
            window.SetThemeMode(original); window.GoHome();
        }
    }
}
