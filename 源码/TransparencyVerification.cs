using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace FileDockDemo;
internal static class TransparencyVerification
{
    internal static void Run(MainWindow w,string root,Action<bool,string> check)
    {
        int original=w.State.EmptyAreaTransparency;
        try{
            check(JsonSerializer.Deserialize<AppState>("{}")!.EmptyAreaTransparency==60,"Old settings default to sixty percent empty-area transparency");
            w.GoHome();w.ExpandPanel();w.SelectPage("Files",false);
            foreach(string theme in new[]{"Dark","Light"}){
                w.SetThemeMode(theme);w.SetEmptyAreaTransparency(60);w.UpdateLayout();
                var bitmap=new RenderTargetBitmap((int)w.ActualWidth,(int)w.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(w);
                byte Alpha(Point p){var data=new byte[4];bitmap.CopyPixels(new Int32Rect((int)p.X,(int)p.Y,1,1),data,4,0);return data[3];}
                check(Alpha(new Point(8,w.ActualHeight/2))==102,$"Empty panel area has genuine forty percent alpha in {theme}");
                var content=w.FileSearchSurface.TranslatePoint(new Point(8,8),w);check(Alpha(content)>=200 && Alpha(content)<240 && w.Opacity==1,$"Search uses translucent input fill with fully opaque text in {theme}");
                check(ThemePalette.Brush("Surface").Color.A==255 && ThemePalette.Brush("Card").Color.A==255,$"Dialogs retain opaque base theme resources in {theme}");
                check(ThemePalette.Brush("ContentSurface").Color.A==85 && ThemePalette.Brush("PaneCard").Color.A==128 && ThemePalette.Brush("PaneInput").Color.A==179,"Panel layers have distinct alpha without changing dialog resources");DemoVerification.SaveRender(w,Path.Combine(root,"transparent-"+theme.ToLowerInvariant()+".png"));
            }
            w.SetEmptyAreaTransparency(-20,false);check(w.State.EmptyAreaTransparency==0,"Transparency clamps lower bound");
            w.SetEmptyAreaTransparency(0,false);check(((SolidColorBrush)w.PanelSurface.Background).Color.A==255,"Zero transparency restores a solid panel background");
            w.SetEmptyAreaTransparency(500,false);check(w.State.EmptyAreaTransparency==85,"Transparency preview clamps upper bound");
            w.SetEmptyAreaTransparency(60);check(w.Storage.Load().EmptyAreaTransparency==60,"Empty-area transparency persists without altering JSON format");
            check(w.CreateTransparencyMenu().Items.Count==2,"Transparency menu exposes slider and default reset");
        }finally{w.SetEmptyAreaTransparency(original);}
    }
}
