using FileDockDemo.Contracts;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FileDockDemo;
public partial class MainWindow : IModuleHost
{
    private Grid? _planShade;
    internal IModuleDialog? ActiveModuleDialog { get; private set; }
    private IInputElement? _planReturnFocus;
    private void OpenModuleDialog(IModuleDialog card)
    {
        CloseModuleDialog();_planReturnFocus=Keyboard.FocusedElement;ActiveModuleDialog=card;_inDialog=true;
        ContentGrid.IsEnabled=false;PageRail.IsEnabled=false;PanelResizeGrip.IsEnabled=false;
        _planShade=new Grid{Background=new SolidColorBrush(Color.FromArgb(128,0,0,0))};Grid.SetColumn(_planShade,Grid.GetColumn(PanelSurface));Panel.SetZIndex(_planShade,50);
        var border=new Border{Child=card.View,CornerRadius=new CornerRadius(16),BorderThickness=new Thickness(1),Margin=new Thickness(14),Padding=new Thickness(18),MaxWidth=520,VerticalAlignment=VerticalAlignment.Center,Effect=new System.Windows.Media.Effects.DropShadowEffect{BlurRadius=24,ShadowDepth=6,Direction=270,Opacity=0.28,Color=Colors.Black}};
        border.SetResourceReference(Border.BackgroundProperty,"Surface");border.SetResourceReference(Border.BorderBrushProperty,"PaneOutline");
        void Size(){border.MaxHeight=Math.Max(200,ActualHeight*.85);border.Padding=new Thickness(ActualWidth<408 ? 16:20);}
        Size();_planShade.SizeChanged+=(_,_)=>Size();_planShade.Children.Add(border);PageRoot.Children.Add(_planShade);
        if (!App.IsSelfTest && SystemParameters.ClientAreaAnimation)
        {
            var scale = new ScaleTransform(0.97, 0.97);
            border.RenderTransformOrigin = new Point(0.5, 0.5);
            border.RenderTransform = scale;
            border.Opacity = 0;
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(160);
            border.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, duration) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(0.97, 1, duration) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new System.Windows.Media.Animation.DoubleAnimation(0.97, 1, duration) { EasingFunction = ease });
        }
        _planShade.MouseDown+=(_,e)=>{if(ReferenceEquals(e.OriginalSource,_planShade)){e.Handled=true;if(!card.HasChanges)card.RequestClose();}};
        Dispatcher.BeginInvoke(()=>card.FocusInitial());
    }
    internal void CloseModuleDialog(IModuleDialog? expected=null)
    {
        if(_planShade==null || expected!=null && !ReferenceEquals(expected,ActiveModuleDialog))return;var closing=ActiveModuleDialog;PageRoot.Children.Remove(_planShade);_planShade=null;ActiveModuleDialog=null;closing?.OnClosed();_inDialog=false;
        ContentGrid.IsEnabled=true;PageRail.IsEnabled=true;PanelResizeGrip.IsEnabled=true;
        var old=_planReturnFocus;Dispatcher.BeginInvoke(()=>{if(old is UIElement el && el.IsVisible && el.IsEnabled)el.Focus();else closing?.RestoreFocus();});QueueAfterPanelInteraction();
    }
    void IModuleHost.ShowDialog(IModuleDialog dialog)=>OpenModuleDialog(dialog);
    void IModuleHost.CloseDialog(IModuleDialog dialog)=>CloseModuleDialog(dialog);
    void IModuleHost.ShowStatus(string message)=>SetStatus(message);
    bool IModuleHost.ConfirmDiscardChanges()=>App.IsSelfTest || MessageBox.Show(this,"放弃已填写的内容？","文件坞",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes;
    bool IModuleHost.CanRestorePopupFocus=>IsActive && !CaptureInteractionHeld;
}
