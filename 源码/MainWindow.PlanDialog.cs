using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FileDockDemo;
public partial class MainWindow
{
    private Grid? _planShade;
    internal PlanCard? ActivePlanCard { get; private set; }
    private IInputElement? _planReturnFocus;
    internal void OpenPlanDialog(PlanCard card)
    {
        ClosePlanDialog();_planReturnFocus=Keyboard.FocusedElement;ActivePlanCard=card;_inDialog=true;
        ContentGrid.IsEnabled=false;PageRail.IsEnabled=false;PanelResizeGrip.IsEnabled=false;
        _planShade=new Grid{Background=new SolidColorBrush(Color.FromArgb(150,0,0,0))};Grid.SetColumn(_planShade,Grid.GetColumn(PanelSurface));Panel.SetZIndex(_planShade,50);
        var border=new Border{Child=card,CornerRadius=new CornerRadius(12),BorderThickness=new Thickness(1),Margin=new Thickness(16),Padding=new Thickness(20),MaxWidth=520,VerticalAlignment=VerticalAlignment.Center};
        border.SetResourceReference(Border.BackgroundProperty,"Surface");border.SetResourceReference(Border.BorderBrushProperty,"Border");
        void Size(){border.MaxHeight=Math.Max(200,ActualHeight*.85);border.Padding=new Thickness(ActualWidth<440 ? 16:20);}
        Size();_planShade.SizeChanged+=(_,_)=>Size();_planShade.Children.Add(border);PageRoot.Children.Add(_planShade);
        _planShade.MouseDown+=(_,e)=>{if(ReferenceEquals(e.OriginalSource,_planShade)){e.Handled=true;if(!card.HasChanges)card.RequestClose();}};
        Dispatcher.BeginInvoke(()=>card.FocusTitle());
    }
    internal void ClosePlanDialog()
    {
        if(_planShade==null)return;PageRoot.Children.Remove(_planShade);_planShade=null;ActivePlanCard=null;_inDialog=false;
        ContentGrid.IsEnabled=true;PageRail.IsEnabled=true;PanelResizeGrip.IsEnabled=true;
        var old=_planReturnFocus;Dispatcher.BeginInvoke(()=>{if(old is UIElement el && el.IsVisible && el.IsEnabled)el.Focus();else Planner.FocusAdd();});QueueAfterPanelInteraction();
    }
}
