using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Input;

namespace FileDockDemo.Features.Planner;
internal sealed class PlanTitleBox : TextBox
{
    private Hint? _hint;
    private bool _composing;
    internal PlanTitleBox()
    {
        FontSize=13;VerticalContentAlignment=VerticalAlignment.Top;HorizontalContentAlignment=HorizontalAlignment.Left;
        Loaded+=(_,_)=>{var layer=AdornerLayer.GetAdornerLayer(this);if(layer!=null && _hint==null){_hint=new Hint(this);layer.Add(_hint);}};
        Unloaded+=(_,_)=>{if(_hint!=null){AdornerLayer.GetAdornerLayer(this)?.Remove(_hint);_hint=null;}};
        TextChanged+=(_,_)=>_hint?.InvalidateVisual();SizeChanged+=(_,_)=>_hint?.InvalidateVisual();
        TextCompositionManager.AddPreviewTextInputStartHandler(this,(_,_)=>{_composing=true;_hint?.InvalidateVisual();});
        TextCompositionManager.AddPreviewTextInputUpdateHandler(this,(_,e)=>{_composing=!string.IsNullOrEmpty(e.TextComposition.CompositionText);_hint?.InvalidateVisual();});
        PreviewTextInput+=(_,_)=>{_composing=false;_hint?.InvalidateVisual();};LostKeyboardFocus+=(_,_)=>{_composing=false;_hint?.InvalidateVisual();};
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if(e.Property==ForegroundProperty || e.Property==FontSizeProperty || e.Property==FontFamilyProperty || e.Property==FlowDirectionProperty)_hint?.InvalidateVisual();
    }
    protected override void OnDpiChanged(DpiScale oldDpi,DpiScale newDpi){base.OnDpiChanged(oldDpi,newDpi);_hint?.InvalidateVisual();}
    private sealed class Hint : Adorner
    {
        private readonly PlanTitleBox _box;
        internal Hint(PlanTitleBox box):base(box){_box=box;IsHitTestVisible=false;}
        protected override void OnRender(DrawingContext dc)
        {
            if(_box.Text.Length!=0 || _box._composing || !_box.IsVisible)return;
            Rect caret;try{caret=_box.GetRectFromCharacterIndex(0,true);}catch(InvalidOperationException){return;}
            if(caret.IsEmpty || caret.Height<=0)return;
            var brush=_box.TryFindResource("PlanSecondary") as Brush??_box.Foreground;
            var text=new FormattedText("输入计划内容",CultureInfo.CurrentUICulture,_box.FlowDirection,new Typeface(_box.FontFamily,_box.FontStyle,_box.FontWeight,_box.FontStretch),_box.FontSize,brush,VisualTreeHelper.GetDpi(_box).PixelsPerDip){LineHeight=caret.Height};
            dc.PushClip(new RectangleGeometry(new Rect(_box.RenderSize)));dc.DrawText(text,new Point(caret.Left,caret.Top));dc.Pop();
        }
    }
}
