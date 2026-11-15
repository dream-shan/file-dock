using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FileDockDemo;

internal static class ShortcutVerification
{
    // Registration is the operating-system boundary. All transaction and dispatch logic is real.
    internal static async Task Run(Action<bool, string> check)
    {
        var active = new Dictionary<int, (uint Modifiers, uint Key)>();
        var denied = new HashSet<uint>();
        int attempts = 0;
        bool Register(int id, uint modifiers, uint key)
        {
            attempts++;
            if (denied.Contains(key) || active.Values.Any(v => v == (modifiers, key))) return false;
            active.Add(id, (modifiers, key)); return true;
        }
        using var registry = new ShortcutRegistry(Register, id => active.Remove(id));
        var original = new ShortcutBinding(true, 3, 0x44);
        var pin = new ShortcutBinding(true, 3, 0x54);
        check(registry.Initialize(ShortcutCommand.ToggleDock, original).Success, "Initial dock shortcut registers");
        if (!registry.IsRegistered(ShortcutCommand.ToggleDock)) return;
        int oldId = active.Single().Key;
        check(active[oldId] == (0x4003u, 0x44u) && registry.TryResolve(oldId, out var first) && first == ShortcutCommand.ToggleDock,
            "Registration adds MOD_NOREPEAT and dispatch resolves command");
        check(registry.Initialize(ShortcutCommand.ToggleWindowPin, pin).Success, "Independent pin shortcut registers");
        int before = attempts;
        check(!registry.Initialize(ShortcutCommand.ToggleDock, original).Success && attempts == before,
            "Repeated initialization cannot orphan an active registration");

        foreach (var bad in new[] {
            new ShortcutBinding(true, 0, 0x44), new ShortcutBinding(true, 4, 0x44),
            new ShortcutBinding(true, 0x4003, 0x44), new ShortcutBinding(true, 3, 0),
            new ShortcutBinding(true, 3, -1), new ShortcutBinding(true, 3, 256),
            new ShortcutBinding(true, 3, 0x10), new ShortcutBinding(true, 3, 0xA2),
            new ShortcutBinding(true, 3, 0x5B), new ShortcutBinding(true, 3, 0xFF) })
        {
            bool persisted = false;
            var result = await registry.ChangeAsync(ShortcutCommand.ToggleDock, bad, () => { persisted = true; return Task.FromResult(true); });
            check(!result.Success && !persisted && registry.GetBinding(ShortcutCommand.ToggleDock) == original && active.ContainsKey(oldId),
                $"Invalid combination rejected without losing binding ({bad.Modifiers}/{bad.Key})");
        }
        bool duplicatePersisted = false;
        var duplicate = await registry.ChangeAsync(ShortcutCommand.ToggleDock, pin,
            () => { duplicatePersisted = true; return Task.FromResult(true); });
        check(!duplicate.Success && !duplicatePersisted && attempts == before && active.Count == 2,
            "Internal duplicates rejected before OS registration or persistence");

        denied.Add(0x45);
        bool occupiedPersisted = false;
        var occupied = await registry.ChangeAsync(ShortcutCommand.ToggleDock, original with { Key = 0x45 },
            () => { occupiedPersisted = true; return Task.FromResult(true); });
        check(!occupied.Success && !occupiedPersisted && active.ContainsKey(oldId) && registry.GetBinding(ShortcutCommand.ToggleDock) == original && registry.GetError(ShortcutCommand.ToggleDock).Length > 0,
            "System conflict retains original registration and exposes error");
        before = attempts;
        check((await registry.ChangeAsync(ShortcutCommand.ToggleDock, original, () => Task.FromResult(true))).Success && attempts == before && registry.GetError(ShortcutCommand.ToggleDock) == "",
            "Saving unchanged binding avoids duplicate registration and clears stale errors");

        var changed = original with { Key = 0x46 };
        bool observedBoth = false;
        var failedSave = await registry.ChangeAsync(ShortcutCommand.ToggleDock, changed, () => {
            observedBoth = active.Count == 3 && active.ContainsKey(oldId) && active.Keys.All(id => registry.TryResolve(id, out _));
            return Task.FromResult(false);
        });
        check(!failedSave.Success && observedBoth && active.Count == 2 && active.ContainsKey(oldId) && registry.GetBinding(ShortcutCommand.ToggleDock) == original,
            "Failed persistence removes candidate and preserves old key continuously");
        var thrownSave = await registry.ChangeAsync(ShortcutCommand.ToggleDock, changed, () => throw new InvalidOperationException("disk unavailable"));
        check(!thrownSave.Success && active.Count == 2 && active.ContainsKey(oldId), "Thrown persistence error rolls registration back");
        var saved = await registry.ChangeAsync(ShortcutCommand.ToggleDock, changed, () => Task.FromResult(true));
        check(saved.Success && active.Count == 2 && !active.ContainsKey(oldId) && !registry.TryResolve(oldId, out _) && registry.GetBinding(ShortcutCommand.ToggleDock) == changed,
            "Successful save switches binding and retires old dispatch ID");
        var disabled = changed with { Enabled = false };
        check(!(await registry.ChangeAsync(ShortcutCommand.ToggleDock, disabled, () => Task.FromResult(false))).Success && registry.IsRegistered(ShortcutCommand.ToggleDock),
            "Failed disabling save keeps shortcut active");
        check((await registry.ChangeAsync(ShortcutCommand.ToggleDock, disabled, () => Task.FromResult(true))).Success && !registry.IsRegistered(ShortcutCommand.ToggleDock) && active.Count == 1,
            "Successful disabling releases only selected shortcut");
        check((await registry.ChangeAsync(ShortcutCommand.ToggleDock, disabled with { Key = 0x54 }, () => Task.FromResult(true))).Success,
            "Disabled combinations do not conflict with active command");
        check(!(await registry.ChangeAsync(ShortcutCommand.ToggleDock, pin, () => Task.FromResult(true))).Success,
            "Reenabling checks conflicts against other command");
        check((await registry.ChangeAsync(ShortcutCommand.ToggleDock, changed, () => Task.FromResult(true))).Success && registry.IsRegistered(ShortcutCommand.ToggleDock),
            "Reenabling registers the desired combination");
        var gate = new TaskCompletionSource<bool>();
        var pending = registry.ChangeAsync(ShortcutCommand.ToggleDock, changed with { Key = 0x47 }, () => gate.Task);
        var queued = registry.ChangeAsync(ShortcutCommand.ToggleWindowPin, pin with { Key = 0x47 }, () => Task.FromResult(true));
        check(!queued.IsCompleted, "Concurrent shortcut changes serialize until persistence finishes");
        gate.SetResult(true);
        check((await pending).Success && !(await queued).Success && active.Count == 2, "Queued change sees committed conflicts");
        registry.Dispose(); registry.Dispose();
        check(active.Count == 0 && !registry.IsRegistered(ShortcutCommand.ToggleDock) && !registry.TryResolve(oldId, out _), "Disposal releases every registration and is idempotent");
        check(!(await registry.ChangeAsync(ShortcutCommand.ToggleDock, original, () => Task.FromResult(true))).Success,
            "Disposed registry cannot register new combinations");

        using var startup = new ShortcutRegistry((_, _, _) => false, _ => { });
        check(!startup.Initialize(ShortcutCommand.ToggleDock, original).Success && !startup.IsRegistered(ShortcutCommand.ToggleDock) && startup.GetBinding(ShortcutCommand.ToggleDock) == original && startup.GetError(ShortcutCommand.ToggleDock).Length > 0,
            "Startup conflicts retain configured preference but report unregistered state");
    }
}
