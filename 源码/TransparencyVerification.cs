using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace FileDockDemo;
internal static class TransparencyVerification
{
    internal static async Task Run(MainWindow w,string root,Action<bool,string> check)
    {
        int original=w.State.EmptyAreaTransparency;
        try{
            check(JsonSerializer.Deserialize<AppState>("{}")!.EmptyAreaTransparency==60,"Old settings default to sixty percent empty-area transparency");
            w.GoHome();w.ExpandPanel();w.SelectPage("Files",false);
            foreach(string theme in new[]{"Dark","Light"}){
                w.SetThemeMode(theme);w.SetEmptyAreaTransparency(60);w.UpdateLayout();
                var bitmap=new RenderTargetBitmap((int)w.ActualWidth,(int)w.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(w);
                byte Alpha(Point p){var data=new byte[4];bitmap.CopyPixels(new Int32Rect((int)p.X,(int)p.Y,1,1),data,4,0);return data[3];}
                int shell = w.BlankAlpha;
                int OverShell(byte alpha) => shell + (int)Math.Round((255-shell)*alpha/255d);
                check(Alpha(new Point(8,w.ActualHeight/2))==shell,$"Empty panel uses the theme tint in {theme}");
                var content=w.FileSearchSurface.TranslatePoint(new Point(8,8),w);check(Math.Abs(Alpha(content)-OverShell(ThemePalette.Brush("FileHover").Color.A))<=1 && w.Opacity==1,$"Search has a shallow translucent inset without fading text in {theme}");
                var header=w.ContentGrid.TranslatePoint(new Point(w.ContentGrid.ActualWidth/2,2),w);
                var footer=w.StatusText.TranslatePoint(new Point(w.StatusText.ActualWidth-3,w.StatusText.ActualHeight+2),w);
                check(Alpha(header)==shell && Alpha(footer)==shell,$"Header and footer share the shell without additional background strips in {theme}");
                check(ThemePalette.Brush("Surface").Color.A==255 && ThemePalette.Brush("Card").Color.A==255,$"Dialogs retain opaque base theme resources in {theme}");
                check(ThemePalette.Brush("Ink").Color.A==255 && ThemePalette.Brush("Muted").Color.A==255,$"Primary and secondary text stay fully opaque in {theme}");
                DemoVerification.SaveRender(w,Path.Combine(root,"transparent-"+theme.ToLowerInvariant()+".png"));
                w.SelectPage("Performance",false);w.UpdateLayout();
                var performance=new RenderTargetBitmap((int)w.ActualWidth,(int)w.ActualHeight,96,96,PixelFormats.Pbgra32);performance.Render(w);
                byte PerformanceAlpha(Point p){var data=new byte[4];performance.CopyPixels(new Int32Rect((int)p.X,(int)p.Y,1,1),data,4,0);return data[3];}
                var card=w.CpuCard.TranslatePoint(new Point(w.CpuCard.ActualWidth-12,12),w);
                var network=w.NetworkBar.TranslatePoint(new Point(1,1),w);
                check(Math.Abs(PerformanceAlpha(card)-OverShell(ThemePalette.Brush("PaneCard").Color.A))<=1,$"Metric cards composite over the panel tint in {theme}");
                check(PerformanceAlpha(network)==shell,$"Network readings share the shell without a separate background strip in {theme}");
                DemoVerification.SaveRender(w,Path.Combine(root,"transparent-performance-"+theme.ToLowerInvariant()+".png"));w.SelectPage("Files",false);
            }
            w.SetEmptyAreaTransparency(-20,false);check(w.State.EmptyAreaTransparency==0,"Transparency clamps lower bound");
            w.SetEmptyAreaTransparency(0,false);check(((SolidColorBrush)w.PanelSurface.Background).Color.A==255,"Zero transparency restores a solid panel background");
            w.SetEmptyAreaTransparency(500,false);check(w.State.EmptyAreaTransparency==85,"Transparency preview clamps upper bound");
            w.SetEmptyAreaTransparency(60);await w.WaitForSavesAsync();check(w.Storage.Load().EmptyAreaTransparency==60,"Empty-area transparency persists without altering JSON format");
            check(w.CreateTransparencyMenu().Items.Count==2,"Transparency menu exposes slider and default reset");
        }finally{w.SetEmptyAreaTransparency(original);}
    }
}
