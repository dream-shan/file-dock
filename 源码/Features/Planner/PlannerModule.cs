using System;
using System.Windows;
using System.Windows.Threading;
using FileDockDemo.Contracts;
namespace FileDockDemo.Features.Planner;

internal sealed class PlannerModule : IModuleLifecycle
{
    private readonly PlannerView _view;
    private readonly DispatcherTimer _dayTimer;
    private bool _disposed;
    internal bool IsMonitoring=>_dayTimer.IsEnabled;
    internal PlannerModule(PlannerView view,IPlannerStore store,IModuleHost host)
    {
        _view=view;_dayTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(30)};
        _dayTimer.Tick+=CheckDay;_view.IsVisibleChanged+=VisibilityChanged;
        _view.Initialize(new PlannerController(store),host);
        if(_view.IsVisible)OnShown();
    }
    private void VisibilityChanged(object sender,DependencyPropertyChangedEventArgs e){if(_view.IsVisible)OnShown();else OnHidden();}
    private void CheckDay(object? sender,EventArgs e){if(!_disposed && _view.IsVisible && !_view.IsEditing && _view.RenderedDay!=PlanRules.Today)_view.Render();}
    public void OnShown(){if(_disposed)return;_dayTimer.Start();if(!_view.IsEditing)_view.Render();}
    public void OnHidden()=>_dayTimer.Stop();
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;_dayTimer.Stop();_dayTimer.Tick-=CheckDay;
        _view.IsVisibleChanged-=VisibilityChanged;_view.Detach();
    }
}
