using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
namespace FileDockDemo;
public partial class MainWindow
{
    // On older layered windows the background is sharp. Limit transmission there
    // so the same foreground stays readable even over a white document.
    internal static byte PanelTintAlpha(int transparency, bool layered) => (byte)Math.Round(255 * (1 -
        Math.Clamp(transparency, 0, 85) / 100d * (layered ? 0.26 : 1)));
    internal byte BlankAlpha => PanelTintAlpha(State.EmptyAreaTransparency, AllowsTransparency);
    private void ApplyBlankTransparency()
    {
        var color=ThemePalette.Brush("Surface").Color;color.A=BlankAlpha;
        var brush=new SolidColorBrush(color);brush.Freeze();Resources["BlankSurface"]=brush;
    }
    internal void SetEmptyAreaTransparency(int percent,bool persist=true)
    {
        State.EmptyAreaTransparency=Math.Clamp(percent,0,85);ApplyBlankTransparency();if(persist)Persist();
    }
    internal MenuItem CreateTransparencyMenu()
    {
        int original=State.EmptyAreaTransparency;
        var menu=new MenuItem{Header=$"空白区域透明度 · {original}%"};
        var content=new StackPanel{Margin=new Thickness(8)};
        var text=new TextBlock{Text="空白更通透，内容保持清晰",Margin=new Thickness(0,0,0,8)};text.SetResourceReference(TextBlock.ForegroundProperty,"Ink");content.Children.Add(text);
        var slider=new Slider{Minimum=0,Maximum=85,TickFrequency=5,IsSnapToTickEnabled=true,Value=original,Width=180,SmallChange=5,LargeChange=10};System.Windows.Automation.AutomationProperties.SetName(slider,"空白区域透明度");content.Children.Add(slider);
        slider.ValueChanged+=(_,_)=>{int value=(int)Math.Round(slider.Value);SetEmptyAreaTransparency(value,false);menu.Header=$"空白区域透明度 · {value}%";};
        menu.Items.Add(new MenuItem{Header=content,StaysOpenOnClick=true});
        var reset=new MenuItem{Header="恢复默认（60%）"};reset.Click+=(_,_)=>slider.Value=60;menu.Items.Add(reset);
        menu.SubmenuClosed+=(_,_)=>{if(State.EmptyAreaTransparency!=original){Persist();original=State.EmptyAreaTransparency;}};
        return menu;
    }
}
