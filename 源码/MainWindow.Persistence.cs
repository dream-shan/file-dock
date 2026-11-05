using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;

namespace FileDockDemo;

public partial class MainWindow
{
    private readonly StateSaveCoordinator _saves;
    private Task _pinOperations=Task.CompletedTask;
    private bool _exitSaving;

    private void ReportSave(Exception? error)
    {
        bool failed=_saveFailed;_saveFailed=error!=null;
        if(error!=null){DiagnosticLog.Error("state-save-failed",error);SetStatus("配置保存失败："+error.Message,true);}
        else if(failed)SetStatus("配置已保存。");
    }

    private void QueuePinOperation(Func<Task> operation)
    {
        if(_exitSaving)return;
        var task=operation();_pinOperations=_pinOperations.IsCompleted?task:Task.WhenAll(_pinOperations,task);
    }

    private async Task<bool> SavePinsAsync(Action<List<PinnedEntry>> change)
    {
        while(true)
        {
            bool saved=await _saves.CommitAsync(candidate=>{
                change(candidate.Pins);for(int i=0;i<candidate.Pins.Count;i++)candidate.Pins[i].Order=i;
            },candidate=>State.Pins=candidate.Pins);
            if(saved){PruneDirectoryKinds();ApplyFilters();return true;}
            if(App.IsSelfTest||MessageBox.Show(this,"保存失败，原有常用入口未改变。是否重试此次操作？", "文件坞",
                MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.Yes)!=MessageBoxResult.Yes)return false;
        }
    }

    internal async Task<bool> WaitForSavesAsync()
    {
        await _pinOperations;
        await Planner.WaitForIdleAsync();
        return await _saves.FlushAsync();
    }

    private async Task QuitAsync()
    {
        if(_exitSaving||_quitting)return;
        RecordDiagnosticState("user-exit-request");
        _exitSaving=true;bool enabled=IsEnabled;IsEnabled=false;_plannerModule.OnHidden();
        try
        {
            await _pinOperations;await Planner.WaitForIdleAsync();
            if(ActiveModuleDialog!=null){ActiveModuleDialog.RequestClose();if(ActiveModuleDialog!=null)return;}
            bool saved=await _saves.FlushForExitAsync(()=>MessageBox.Show(this,
                "配置仍未保存。\n\n是：重试保存\n否：放弃尚未保存的设置并退出\n取消：返回程序", "文件坞",
                MessageBoxButton.YesNoCancel,MessageBoxImage.Warning,MessageBoxResult.Cancel) switch {
                    MessageBoxResult.Yes=>SaveExitChoice.Retry,MessageBoxResult.No=>SaveExitChoice.Discard,_=>SaveExitChoice.Cancel});
            if(saved){_exitSaving=false;_quitting=true;Application.Current.Shutdown();}
        }
        finally
        {
            if(!_quitting){_exitSaving=false;IsEnabled=enabled;if(IsVisible&&IsPlannerPage)_plannerModule.OnShown();}
        }
    }
}
