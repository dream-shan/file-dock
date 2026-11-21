using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace FileDockDemo;

internal static class AppMappingRuntimeVerification
{
    internal static void Run(Action<bool,string> check)
    {
        var desktop = new TestDesktop();
        using var runtime = new AppMappingRuntime(desktop);
        var group = new AppShortcutGroup {
            Name = "Test editor", Path = @"C:\Editors\Edit.exe",
            Manual = [new() { Action = "Save", Keys = "Shift+S", Scope = ManualShortcutScope.Application,
                Mapping = new() { Enabled = true, Modifiers = 3, Key = 'M' } }]
        };
        var data = Marshal.AllocHGlobal(32);
        try
        {
            bool Key(int key = 'M', bool up = false, int flags = 0)
            {
                Marshal.WriteInt32(data, 0, key);
                Marshal.WriteInt32(data, 8, flags);
                Marshal.WriteIntPtr(data, 16, IntPtr.Zero);
                return runtime.Handle(0, (IntPtr)(up ? 0x105 : 0x104), data);
            }

            check(!Key() && desktop.ForegroundReads == 0, "Empty mapping set passes input through");
            runtime.Update([group]);
            desktop.Held.UnionWith([0xA2, 0xA3, 0xA4]); // Both Ctrl keys and left Alt.
            check(!Key('Q') && !Key(flags: 0x10) && desktop.ForegroundReads == 0,
                "Unconfigured and injected events never look up the foreground");
            desktop.Image = @"D:\Others\Edit.exe";
            check(!Key() && desktop.Batches.Count == 0, "Executable name alone never selects a mapping");
            desktop.Image = group.Path;
            desktop.SwitchDuringLookup = true;
            check(!Key() && desktop.Batches.Count == 0, "Changing the foreground before delivery cancels a mapping");
            desktop.SwitchDuringLookup = false;
            var globals = new Dictionary<string,ShortcutBinding> {
                ["test"] = new() { Enabled = true, Modifiers = 3, Key = 'M' }
            };
            runtime.Update([group], globals);
            check(!Key() && desktop.Batches.Count == 0, "Global shortcut registered on the alias wins");
            globals["test"] = new() { Enabled = true, Modifiers = 4, Key = 'S' };
            runtime.Update([group], globals);
            check(!Key() && desktop.Batches.Count == 0, "Original chord cannot trigger a registered global shortcut");
            globals.Remove("test");
            globals[ShortcutCatalog.Preview] = new() { Enabled = true, Modifiers = 4, Key = 'S' };
            runtime.Update([group], globals);
            desktop.Held.Add(0xA5);
            check(!Key() && desktop.Batches.Count == 0, "AltGr typing passes through");
            desktop.Held.Remove(0xA5);
            desktop.Held.Add('M');
            check(!Key() && desktop.Batches.Count == 0, "An already held pass-through key cannot start a mapping");
            desktop.Held.Remove('M');
            check(Key() && desktop.Batches.Count == 1, "Configured physical alias dispatches once");
            var batch = desktop.Batches.Single();
            int original = Array.IndexOf(batch, new AppMappingStroke('S', false));
            check(original >= 0 && batch[original + 1] == new AppMappingStroke('S', true) &&
                batch.Take(original).Where(s => s.Key != 0xFF).ToHashSet().SetEquals([new(0xA2,true),new(0xA3,true),new(0xA4,true),new(0xA0,false)]) &&
                batch.Skip(original + 2).Where(s => s.Key != 0xFF).ToHashSet().SetEquals([new(0xA2,false),new(0xA3,false),new(0xA4,false),new(0xA0,true)]),
                "Original chord releases and restores both physical modifier sides");
            desktop.Image = @"D:\Others\Edit.exe";
            runtime.Update([]);
            check(Key() && !Key(up:true,flags:0x10) && Key(up:true) && !Key() && desktop.Batches.Count == 1,
                "Held alias consumes repeats and its physical release after switching apps and disabling mappings");
            runtime.Update([group]);
            desktop.Image = group.Path;
            desktop.Accepted = 0;
            check(!Key() && !Key(up:true), "A blocked send leaves the user's alias available");
            desktop.Accepted = 1;
            int before = desktop.Batches.Count;
            check(!Key() && desktop.Batches.Count == before + 2 &&
                desktop.Batches[^1].SequenceEqual([new AppMappingStroke(desktop.Batches[^2][0].Key, false)]),
                "Partial delivery restores the modifier that was actually released");
            before = desktop.Batches.Count;
            desktop.AcceptedCounts = new Queue<int>([original + 1, 1, int.MaxValue]);
            check(Key() && Key(up:true) && desktop.Batches.Count == before + 3 &&
                desktop.Batches[^2].First() == new AppMappingStroke('S', true) &&
                desktop.Batches[^1].SequenceEqual(desktop.Batches[^2].Skip(1)),
                "A partly accepted cleanup continues with only the outstanding key transitions");
            desktop.Held.Clear();
            desktop.Held.Add(0xA4);
            group.Manual[0].Keys = "F2";
            group.Manual[0].Mapping = new() { Enabled = true, Modifiers = 1, Key = 'J' };
            runtime.Update([group]);
            desktop.Accepted = int.MaxValue;
            AppMappingStroke[] guarded = [new(0xFF,false),new(0xFF,true),new(0xA4,true),
                new(0x71,false),new(0x71,true),new(0xA4,false),new(0xFF,false),new(0xFF,true)];
            check(Key('J') && Key('J',up:true) && desktop.Batches[^1].SequenceEqual(guarded),
                "Alt+J to F2 masks both synthetic Alt release and the eventual physical release");
            for (int cut = 1; cut < guarded.Length; cut++)
            {
                before = desktop.Batches.Count;
                desktop.AcceptedCounts = new([cut]);
                bool handled = Key('J');
                var accepted = desktop.Batches[before].Take(cut)
                    .Concat(desktop.Batches.Skip(before + 1).SelectMany(part => part)).ToArray();
                bool restored = cut < 3 || accepted.TakeLast(3).SequenceEqual(guarded.TakeLast(3));
                check(handled == (cut >= 4) && Key('J',up:true) == handled && restored &&
                    accepted.Count(s => s == new AppMappingStroke(0x71,false)) == (handled ? 1 : 0) &&
                    accepted.Count(s => s == new AppMappingStroke(0xFF,false)) ==
                    accepted.Count(s => s == new AppMappingStroke(0xFF,true)),
                    $"Partial Alt mapping preserves menu masking and original-key delivery at cut {cut}");
            }
            runtime.Dispose();
            check(!Key(), "Disposed runtime leaves physical input alone");
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    private sealed class TestDesktop : IAppMappingDesktop
    {
        internal readonly HashSet<int> Held = [];
        internal readonly List<AppMappingStroke[]> Batches = [];
        internal int ForegroundReads, Accepted = int.MaxValue;
        internal Queue<int> AcceptedCounts = new();
        internal bool SwitchDuringLookup;
        internal string? Image;
        private int window = 42;
        public bool Down(int key) => Held.Contains(key);
        public AppMappingWindow Foreground()
        {
            ForegroundReads++;
            return new((IntPtr)window, 7, 11);
        }
        public string? ImagePath(AppMappingWindow target)
        {
            if (SwitchDuringLookup) window++;
            return Image;
        }
        public int Send(AppMappingStroke[] strokes)
        {
            Batches.Add(strokes);
            return Math.Min(AcceptedCounts.TryDequeue(out int next) ? next : Accepted, strokes.Length);
        }
    }
}
