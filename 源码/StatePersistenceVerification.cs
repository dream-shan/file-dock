using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using FileDockDemo.Features.Planner;
using FileDockDemo.Infrastructure;

namespace FileDockDemo;

internal static class StatePersistenceVerification
{
    private static readonly TimeSpan Limit=TimeSpan.FromSeconds(15);
    internal static async Task Run(MainWindow window,string root,Action<bool,string> check)
    {
        var state=new AppState{Pins=new(){new PinnedEntry{Path="C:\\fixture.txt"}},
            Plans=new(){new PlanItem{Title="original",ScheduleSchema=1}},
            Countdowns=new(){new CountdownItem{Title="date"}}};
        var copy=state.Copy();state.Pins[0].DisplayName="changed";state.Plans[0].Title="changed";state.Countdowns[0].Title="changed";
        check(copy.Pins[0].DisplayName==""&&copy.Plans[0].Title=="original"&&copy.Countdowns[0].Title=="date",
            "Save snapshots detach every mutable business list and item");

        int writes=0;AppState? disk=null;
        using(var saves=new StateSaveCoordinator(state,s=>{Interlocked.Increment(ref writes);disk=s;},_=>{}))
        {
            for(int i=0;i<100;i++){state.PanelWidth=380+i;saves.RequestSettingsSave();}
            check(writes==0&&saves.HasPendingSettings,"Rapid settings changes do not synchronously write to disk");
            await saves.Pending.WaitAsync(Limit);
            check(writes==1&&disk?.PanelWidth==479&&!saves.HasPendingSettings,"Settings bursts coalesce into one save of the latest value");
        }

        state=new AppState{Pins=new(){new PinnedEntry{Path="C:\\old.txt"}},Plans=new(){new PlanItem{Title="before",ScheduleSchema=1}}};
        var snapshots=new List<AppState>();int running=0,maximum=0,writerThread=0;
        int uiThread=Environment.CurrentManagedThreadId;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using(var release=new ManualResetEventSlim())
        using(var saves=new StateSaveCoordinator(state,s=>{
            writerThread=Environment.CurrentManagedThreadId;int count=Interlocked.Increment(ref running);maximum=Math.Max(maximum,count);
            try{entered.TrySetResult();if(!release.Wait(Limit))throw new TimeoutException("fixture-writer");snapshots.Add(s.Copy());}
            finally{Interlocked.Decrement(ref running);}
        },_=>{}))
        {
            Task<bool>? business=null,flush=null;
            try
            {
                state.PanelWidth=500;saves.RequestSettingsSave();
                business=saves.CommitAsync(c=>c.Pins.Add(new PinnedEntry{Path="C:\\new.txt"}),c=>state.Pins=c.Pins);
                await entered.Task.WaitAsync(Limit);
                check(!business.IsCompleted&&state.Pins.Count==1,"Business edits remain invisible while their snapshot is being written");
                bool responsive=false;await Dispatcher.CurrentDispatcher.InvokeAsync(()=>responsive=true);
                check(responsive&&writerThread!=uiThread,"Slow configuration writes leave the UI dispatcher responsive");
                state.PanelWidth=600;state.ThemeMode="Dark";state.Plans[0].Title="live edit";saves.RequestSettingsSave();
                flush=saves.FlushAsync();
                check(!flush.IsCompleted&&snapshots.Count==0,"Explicit flushes queue behind an in-flight business save");
            }
            finally{release.Set();if(business!=null)await business.WaitAsync(Limit);if(flush!=null)await flush.WaitAsync(Limit);}
            check(state.Pins.Count==2&&state.PanelWidth==600&&state.ThemeMode=="Dark"&&state.Plans[0].Title=="live edit",
                "Business completion merges only its own fields and preserves newer settings and unrelated edits");
            check(snapshots.Count==2&&snapshots[0].PanelWidth==500&&snapshots[0].Plans[0].Title=="before"&&
                snapshots[1].PanelWidth==600&&snapshots[1].Pins.Count==2&&snapshots[1].ThemeMode=="Dark",
                "Queued saves use detached snapshots and never overwrite newer data with an older snapshot");
            check(maximum==1&&!saves.HasPendingSettings,"Business writes and settings flushes share one serial writer");
        }

        state=new AppState();bool fail=true;Exception? reported=null;writes=0;
        using(var saves=new StateSaveCoordinator(state,s=>{writes++;if(fail)throw new IOException("fixture-storage");disk=s;},e=>reported=e))
        {
            state.ThemeMode="Dark";saves.RequestSettingsSave();await saves.Pending.WaitAsync(Limit);
            check(saves.HasPendingSettings&&reported is IOException,"Settings save failures retain pending changes and report the error");
            var original=state.Pins;
            check(!await saves.CommitAsync(c=>c.Pins.Add(new PinnedEntry{Path="C:\\rejected.txt"}),c=>state.Pins=c.Pins)&&
                ReferenceEquals(state.Pins,original)&&state.Pins.Count==0,"Failed business saves leave the original list and records intact");
            fail=false;check(await saves.FlushAsync()&&reported==null&&!saves.HasPendingSettings&&disk?.ThemeMode=="Dark",
                "Retrying a failed settings save clears the error only after a successful latest snapshot");
            var store=new AppStatePlannerStore(state,saves.CommitAsync);var controller=new PlannerController(store);
            var first=controller.SaveAsync(new PlanItem{Title="first",ScheduleSchema=1});
            var second=controller.SaveAsync(new PlanItem{Title="second",ScheduleSchema=1});
            var date=store.WriteCountdownsAsync(new[]{new CountdownItem{Title="date"}});
            check((await Task.WhenAll(first,second,date)).All(result=>result)&&state.Plans.Count==2&&state.Countdowns.Count==1,
                "Concurrent planner operations read the latest committed data and preserve other modules");
            var plans=store.Read();plans[0].Title="outside mutation";var dates=store.ReadCountdowns();dates[0].Title="outside mutation";
            check(state.Plans[0].Title=="first"&&state.Countdowns[0].Title=="date","Planner store reads cannot mutate committed records through shared references");
        }

        foreach(var choice in new[]{SaveExitChoice.Retry,SaveExitChoice.Cancel,SaveExitChoice.Discard})
        {
            writes=0;int decisions=0;state=new AppState();
            using var saves=new StateSaveCoordinator(state,_=>{writes++;if(writes==1||choice!=SaveExitChoice.Retry)throw new IOException("fixture-exit");},_=>{});
            state.ThemeMode="Dark";saves.RequestSettingsSave();
            bool exit=await saves.FlushForExitAsync(()=>{decisions++;return choice;});
            check(decisions==1&&exit==(choice!=SaveExitChoice.Cancel)&&writes==(choice==SaveExitChoice.Retry?2:1),
                "Exit save failure honors explicit retry, cancel or discard: "+choice);
            check(saves.HasPendingSettings==(choice!=SaveExitChoice.Retry),"Only a successful exit retry marks pending settings saved: "+choice);
        }

        writes=0;state=new AppState();var disposed=new StateSaveCoordinator(state,_=>writes++,_=>{});
        disposed.RequestSettingsSave();Task pending=disposed.Pending;disposed.Dispose();disposed.Dispose();await pending.WaitAsync(Limit);
        check(writes==0&&!await disposed.FlushAsync(),"Disposal cancels deferred settings writes without starting new work");

        string path=Path.Combine(root,"async-state-store");var storage=new StorageService(path);state=new AppState();
        using(var saves=new StateSaveCoordinator(state,storage.Save,_=>{}))
        {
            var store=new AppStatePlannerStore(state,saves.CommitAsync);
            check(await store.TryWriteAsync(new[]{new PlanItem{Title="persisted",ScheduleSchema=1}})&&storage.Load().Plans.Single().Title=="persisted",
                "Async business saves retain the existing JSON envelope and storage rules");
            state.ThemeMode="Dark";await saves.FlushAsync();File.WriteAllText(Path.Combine(path,"state.json"),"{broken");
            var recovered=storage.Load();
            check(recovered.Plans.Single().Title=="persisted","Atomic async writes retain a recoverable previous configuration");
            check(!Directory.EnumerateFiles(path,"*.tmp").Any(),"Atomic async writes leave no temporary state files after completion");
        }

        await window.WaitForSavesAsync();var pins=window.State.Pins;
        try
        {
            using(var locked=new FileStream(Path.Combine(root,"data","state.json"),FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                window.AddPins(new[]{Path.Combine(root,"pin-save-fixture.txt")});
                check(!await window.WaitForSavesAsync()&&ReferenceEquals(window.State.Pins,pins),
                    "Real pin save failure preserves the original UI data instead of applying an unsaved entry");
            }
            window.AddPins(new[]{Path.Combine(root,"pin-save-fixture.txt")});await window.WaitForSavesAsync();
            check(window.State.Pins.Count==pins.Count+1,"A failed pin operation can be retried successfully after the file lock is released");
        }
        finally{window.State.Pins=pins;await window.WaitForSavesAsync();window.ApplyFilters();}
    }
}
