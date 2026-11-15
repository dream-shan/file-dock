using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FileDockDemo;

public partial class MainWindow
{
    private AppLauncherView? _applicationsView;
    private Task _applicationOperations=Task.CompletedTask;
    private int _applicationInteractions;
    internal void RefreshApplicationsForVerification()=>_applicationsView?.Refresh();
    internal void LaunchApplicationForVerification(ApplicationEntry entry)=>_applicationsView?.Launch(entry);
    private void InitializeApplications()
    {
        _applicationsView=new AppLauncherView(this,()=>State.Applications,SaveApplicationsAsync,SetStatus,active=>{
            _applicationInteractions=Math.Max(0,_applicationInteractions+(active?1:-1));
            _inDialog=_applicationInteractions>0;
            if(!_inDialog)QueueDesktopPresence();
        });
        ApplicationsSection.Content=_applicationsView.View;
    }
    private Task<bool> SaveApplicationsAsync(Action<List<ApplicationEntry>> change)
    {
        if(_exitSaving)return Task.FromResult(false);
        async Task<bool> Save()
        {
            bool ok=await _saves.CommitAsync(candidate=>{
                change(candidate.Applications);
                for(int i=0;i<candidate.Applications.Count;i++)candidate.Applications[i].Order=i;
            },candidate=>State.Applications=candidate.Applications);
            if(ok)_applicationsView?.Refresh();
            return ok;
        }
        var operation=Save();
        _applicationOperations=_applicationOperations.IsCompleted?operation:Task.WhenAll(_applicationOperations,operation);
        return operation;
    }
}
