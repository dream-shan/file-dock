using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FileDockDemo;
internal static class LiveFolderVerification
{
    internal static async Task Run(MainWindow window,string directory,Action<bool,string> check)
    {
        string root=Path.Combine(directory,"live-folder"), child=Path.Combine(root,"子目录");
        Directory.CreateDirectory(child);
        string one=Path.Combine(root,"原文件.txt"), two=Path.Combine(root,"保留.txt");
        File.WriteAllText(one,"one"); File.WriteAllText(two,"two");
        EntryView[] Rows()=>window.FolderList.Items.Cast<EntryView>().ToArray();
        async Task Until(Func<bool> condition)
        {
            for(int i=0;i<160;i++){if(condition())return;await Task.Delay(50);}
            throw new InvalidOperationException("Live folder notification did not reach the expected state.");
        }
        window.LiveFolderUpdatesEnabled=true;
        try
        {
            window.ExpandPanel(); await window.NavigateToFolderAsync(root);
            check(window.FolderWatcherActive,"Visible folder starts a directory watcher");
            string created=Path.Combine(root,"新增.txt");File.WriteAllText(created,"new");
            await Until(()=>Rows().Any(e=>e.Path==created));
            check(true,"External file creation appears without manual refresh");
            window.SelectFile(Rows().Single(e=>e.Path==one),false);window.SelectFile(Rows().Single(e=>e.Path==two),true);
            File.Delete(one);await Until(()=>Rows().All(e=>e.Path!=one));
            check(window.SelectedFileCount==1 && Rows().Single(e=>e.Path==two).IsChosen,"Deletion removes stale entries while preserving surviving selection");
            string renamed=Path.Combine(root,"改名.txt");File.Move(created,renamed);
            await Until(()=>Rows().Any(e=>e.Path==renamed)&&Rows().All(e=>e.Path!=created));
            check(true,"External rename replaces the old displayed path");
            File.WriteAllBytes(renamed,new byte[2048]);
            await Until(()=>Rows().Single(e=>e.Path==renamed).Subtitle.StartsWith("2 KB"));
            check(true,"Saving over a file updates its displayed metadata");
            window.SearchBox.Text="匹配";await window.WaitForFolderSearchAsync();
            string nested=Path.Combine(child,"匹配.txt");File.WriteAllText(nested,"nested");
            await Until(()=>Rows().Any(e=>e.Path==nested));
            check(window.SearchBox.Text=="匹配","Recursive search observes child changes without losing its query");
            File.Delete(nested);await Until(()=>Rows().All(e=>e.Path!=nested));
            check(true,"Recursive search evicts externally deleted results");
            window.CollapsePanel();check(!window.FolderWatcherActive,"Collapsing releases directory monitoring");
            string hidden=Path.Combine(child,"匹配-hidden.txt");File.WriteAllText(hidden,"hidden");
            window.ExpandPanel();await Until(()=>Rows().Any(e=>e.Path==hidden));
            check(true,"Expanding reconciles file changes made while monitoring was paused");
            window.SearchBox.Clear();await Until(()=>Rows().Any(e=>e.Path==renamed));
            int before=window.LiveFolderRefreshCount;
            for(int i=0;i<30;i++)File.WriteAllText(Path.Combine(root,$"batch-{i}.txt"),"batch");
            await Until(()=>Rows().Count(e=>e.DisplayName.StartsWith("batch-"))==30);
            check(window.LiveFolderRefreshCount-before<15,"Batch filesystem changes are coalesced instead of refreshing per event");
            await window.NavigateToFolderAsync(child);File.WriteAllText(Path.Combine(root,"旧目录.txt"),"old");
            await Task.Delay(700);
            check(Rows().All(e=>e.Path.StartsWith(child+Path.DirectorySeparatorChar)),"Old directory notifications cannot overwrite a new folder view");
            window.GoHome();check(!window.FolderWatcherActive,"Returning home releases directory monitoring");
        }
        finally{window.LiveFolderUpdatesEnabled=false;window.GoHome();window.CollapsePanel();window.ExpandPanel();}
    }
}
