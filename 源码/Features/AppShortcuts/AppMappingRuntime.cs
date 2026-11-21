using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FileDockDemo.Features.WindowPinning;

namespace FileDockDemo;

internal readonly record struct AppMappingStroke(int Key, bool Up);
internal readonly record struct AppMappingWindow(IntPtr Handle, uint Process, uint Thread);

internal interface IAppMappingDesktop
{
    bool Down(int key);
    AppMappingWindow Foreground();
    string? ImagePath(AppMappingWindow target);
    int Send(AppMappingStroke[] strokes);
}

// Owned by the same UI thread as the existing low-level keyboard hook.
internal sealed class AppMappingRuntime : IDisposable
{
    private static readonly (uint Flag, int Left, int Right)[] Modifiers =
        [(2,0xA2,0xA3), (1,0xA4,0xA5), (4,0xA0,0xA1), (8,0x5B,0x5C)];
    private readonly IAppMappingDesktop desktop;
    private readonly Dictionary<AppChord,Dictionary<string,AppChord>> aliases = [];
    private readonly HashSet<int> candidateKeys = [];
    // Only physically held aliases whose initial press we consumed.
    private readonly HashSet<int> consumed = [];
    private bool disposed;

    internal AppMappingRuntime() : this(new AppMappingDesktop()) { }
    internal AppMappingRuntime(IAppMappingDesktop desktop) => this.desktop = desktop;

    internal void Update(IEnumerable<AppShortcutGroup> groups, IReadOnlyDictionary<string,ShortcutBinding>? globals = null)
    {
        aliases.Clear();
        candidateKeys.Clear();
        if (disposed) return;
        var reserved = globals == null ? null : AppMappingRules.ReservedGlobals(globals);
        foreach (var group in groups)
        {
            foreach (var (alias, original) in AppMappingRules.Active(group))
            {
                if (reserved != null && (reserved.Contains(alias) || reserved.Contains(original))) continue;
                if (!aliases.TryGetValue(alias, out var paths))
                    aliases[alias] = paths = new(StringComparer.OrdinalIgnoreCase);
                paths[Path.GetFullPath(group.Path)] = original;
                candidateKeys.Add(alias.Key);
            }
        }
    }

    internal static bool IsMappingInput(IntPtr data) => data != IntPtr.Zero &&
        (Marshal.ReadInt32(data, 8) & 0x10) != 0 &&
        Marshal.ReadIntPtr(data, 16) == AppMappingDesktop.InputTag;

    internal bool Handle(int code, IntPtr message, IntPtr data)
    {
        if (disposed || code != 0 || data == IntPtr.Zero ||
            (candidateKeys.Count == 0 && consumed.Count == 0)) return false;
        int kind = (int)message;
        if (kind is not (0x100 or 0x101 or 0x104 or 0x105) ||
            (Marshal.ReadInt32(data, 8) & 0x12) != 0) return false;
        int key = Marshal.ReadInt32(data);
        if (kind is 0x101 or 0x105) return consumed.Remove(key);
        if (consumed.Contains(key)) return true;
        if (!candidateKeys.Contains(key) || desktop.Down(key)) return false;

        uint modifiers = 0;
        int sides = 0;
        for (int i = 0; i < Modifiers.Length; i++)
        {
            var mod = Modifiers[i];
            int down = (desktop.Down(mod.Left) ? 1 : 0) | (desktop.Down(mod.Right) ? 2 : 0);
            if (down != 0) modifiers |= mod.Flag;
            sides |= down << (2 * i);
        }
        // Windows implements AltGr as Ctrl + right Alt. Leave text entry intact.
        if ((modifiers & 2) != 0 && (sides & 8) != 0) return false;
        if (!aliases.TryGetValue(new(modifiers, key), out var paths)) return false;
        var target = desktop.Foreground();
        if (target.Handle == IntPtr.Zero || desktop.ImagePath(target) is not { } image ||
            !paths.TryGetValue(image, out var original) || desktop.Down(original.Key)) return false;

        var transitions = new List<AppMappingStroke>(6);
        for (int i = 0; i < Modifiers.Length; i++)
        {
            var mod = Modifiers[i];
            bool required = (original.Modifiers & mod.Flag) != 0;
            int down = (sides >> (2 * i)) & 3;
            if (required && down == 0) transitions.Add(new(mod.Left, false));
            if (!required && (down & 1) != 0) AppendRelease(transitions, mod.Left);
            if (!required && (down & 2) != 0) AppendRelease(transitions, mod.Right);
        }
        var batch = new List<AppMappingStroke>(transitions);
        batch.Add(new(original.Key, false));
        batch.Add(new(original.Key, true));
        for (int i = transitions.Count - 1; i >= 0; i--)
            batch.Add(transitions[i] with { Up = !transitions[i].Up });
        var strokes = batch.ToArray();
        if (desktop.Foreground() != target) return false;
        int sent = desktop.Send(strokes);
        if (sent == 0) return false; // Includes UIPI blocking a more privileged application.
        bool originalPressed = sent > transitions.Count;
        if (sent < strokes.Length) RestorePartial(strokes, sent, originalPressed);
        if (!originalPressed) return false;
        consumed.Add(key);
        return true;
    }

    private static void AppendRelease(List<AppMappingStroke> strokes, int key)
    {
        if (key is 0xA4 or 0xA5)
        {
            // PowerToys Keyboard Manager uses this neutral key pair so a bare
            // Alt release cannot activate the menu. Reversing the prefix also
            // puts the pair immediately after restoring the physical Alt.
            strokes.Add(new(0xFF, false));
            strokes.Add(new(0xFF, true));
        }
        strokes.Add(new(key, true));
    }

    private void RestorePartial(AppMappingStroke[] batch, int sent, bool originalPressed)
    {
        // Once the original key is down, the suffix contains only its release,
        // modifier restoration and the remaining menu protection.
        AppMappingStroke[] cleanup;
        if (originalPressed) cleanup = batch[sent..];
        else
        {
            var rollback = new List<AppMappingStroke>(sent);
            for (int i = sent - 1; i >= 0; i--)
            {
                var stroke = batch[i];
                if (stroke.Up) rollback.Add(new(stroke.Key, false));
                else AppendRelease(rollback, stroke.Key);
            }
            cleanup = rollback.ToArray();
        }
        while (cleanup.Length > 0)
        {
            int restored = desktop.Send(cleanup);
            if (restored == 0 || restored == cleanup.Length) break;
            cleanup = cleanup[restored..];
        }
    }

    public void Dispose()
    {
        disposed = true;
        aliases.Clear();
        candidateKeys.Clear();
        consumed.Clear();
    }
}

internal sealed class AppMappingDesktop : IAppMappingDesktop
{
    internal static readonly IntPtr InputTag = new(0x46444D50);
    public bool Down(int key) => GetAsyncKeyState(key) < 0;
    public AppMappingWindow Foreground()
    {
        IntPtr handle = NativeMethods.GetForegroundWindow();
        uint thread = PinNative.GetWindowThreadProcessId(handle, out uint process);
        return thread == 0 || process == Environment.ProcessId ? default : new(handle, process, thread);
    }
    public string? ImagePath(AppMappingWindow target) => AppShortcutTarget.Read(target.Handle)?.Path;

    public int Send(AppMappingStroke[] strokes)
    {
        var inputs = new Input[strokes.Length];
        for (int i = 0; i < strokes.Length; i++)
        {
            var stroke = strokes[i];
            uint scan = MapVirtualKey((uint)stroke.Key, 4); // MAPVK_VK_TO_VSC_EX
            inputs[i].Type = 1;
            inputs[i].Data.Keyboard = new() { Key = (ushort)stroke.Key, Scan = (ushort)(scan & 0xff),
                Flags = (stroke.Up ? 2u : 0u) | ((scan >> 8) == 0xE0 ? 1u : 0u), Tag = InputTag };
        }
        return (int)SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { internal uint Type; internal InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] internal KeyboardInput Keyboard;
        [FieldOffset(0)] internal MouseInput Mouse; // Determines native union size on both x86 and x64.
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { internal ushort Key, Scan; internal uint Flags, Time; internal IntPtr Tag; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { internal int X, Y; internal uint Buttons, Flags, Time; internal IntPtr Tag; }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")] private static extern uint MapVirtualKey(uint key, uint kind);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
