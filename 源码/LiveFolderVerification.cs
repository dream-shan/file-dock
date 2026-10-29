using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
            string image=Path.Combine(root,"图片.png");
            void WriteImage(byte blue,byte red)
            {
                var encoder=new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[]{blue,0,red,255},4)));
                using var stream=File.Create(image);encoder.Save(stream);
            }
            WriteImage(0,255);await Until(()=>Rows().Any(e=>e.Path==image));
            _=await ImageLoader.LoadAsync(image,false,default);
            var stamp=File.GetLastWriteTimeUtc(image);int imageRefresh=window.LiveFolderRefreshCount;
            WriteImage(255,0);File.SetLastWriteTimeUtc(image,stamp);
            await Until(()=>window.LiveFolderRefreshCount>imageRefresh);
            var updated=await ImageLoader.LoadAsync(image,false,default);
            var converted=new FormatConvertedBitmap(updated!,PixelFormats.Bgra32,null,0);var pixel=new byte[4];converted.CopyPixels(pixel,4,0);
            check(pixel[0]>pixel[2],"Overwriting an image invalidates the thumbnail even when its timestamp is preserved");
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
