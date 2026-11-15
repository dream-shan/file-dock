using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileDockDemo;

public enum ShortcutCommand { ToggleDock, ToggleWindowPin }
public sealed record ShortcutBinding(bool Enabled, uint Modifiers, int Key);
public sealed record ShortcutResult(bool Success, string Error);

public sealed class ShortcutRegistry : IDisposable
{
    private sealed record Entry(ShortcutBinding Binding, int? Id);
    private readonly Func<int, uint, uint, bool> _register;
    private readonly Action<int> _unregister;
    private readonly Dictionary<ShortcutCommand, Entry> _entries = new();
    private readonly Dictionary<ShortcutCommand, string> _errors = new();
    private readonly Dictionary<int, ShortcutCommand> _commands = new();
    private readonly SemaphoreSlim _changes = new(1, 1);
    private int _nextId = 0x5000;
    private bool _disposed;

    // Use from the window dispatcher, including the persistence callback continuation.
    public ShortcutRegistry(Func<int, uint, uint, bool> register, Action<int> unregister)
    {
        _register = register ?? throw new ArgumentNullException(nameof(register));
        _unregister = unregister ?? throw new ArgumentNullException(nameof(unregister));
    }

    public ShortcutBinding? GetBinding(ShortcutCommand command) => _entries.TryGetValue(command, out var entry) ? entry.Binding : null;
    public bool IsRegistered(ShortcutCommand command) => !_disposed && _entries.TryGetValue(command, out var entry) && entry.Id.HasValue;
    public string GetError(ShortcutCommand command) => _errors.TryGetValue(command, out var error) ? error : "";
    public bool TryResolve(int id, out ShortcutCommand command) => _commands.TryGetValue(id, out command);

    public string Validate(ShortcutCommand command, ShortcutBinding binding)
    {
        if (!Enum.IsDefined(command)) return "未知的快捷键命令。";
        if (binding is null) return "快捷键配置不能为空。";
        if ((binding.Modifiers & ~0xFu) != 0 || (binding.Modifiers & 3) == 0)
            return "快捷键至少需要 Ctrl 或 Alt，可同时使用 Shift 或 Win。";
        if (!ValidKey(binding.Key)) return "请选择有效的按键，不能只使用修饰键。";
        if (binding.Enabled)
            foreach (var pair in _entries)
                if (pair.Key != command && pair.Value.Binding.Enabled &&
                    pair.Value.Binding.Modifiers == binding.Modifiers && pair.Value.Binding.Key == binding.Key)
                    return "该组合键已用于另一项应用快捷键。";
        return "";
    }

    private static bool ValidKey(int key) => key is 0x08 or 0x09 or 0x0C or 0x0D or 0x13 or 0x14 or 0x1B or 0x90 or 0x91 or 0xE2 ||
        key is >= 0x20 and <= 0x2F or >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or
        >= 0x60 and <= 0x87 or >= 0xA6 and <= 0xB7 or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF;

    public ShortcutResult Initialize(ShortcutCommand command, ShortcutBinding binding)
    {
        if (_disposed) return Fail(command, "快捷键服务已关闭。");
        if (_entries.ContainsKey(command)) return Fail(command, "该命令的快捷键已初始化。");
        string error = Validate(command, binding);
        if (error.Length > 0) return Fail(command, error);
        // Keep the configured preference even when Windows cannot register it at startup.
        _entries[command] = new(binding, null);
        if (!binding.Enabled) return Success(command);
        var result = Register(command, binding, out int? id);
        if (!result.Success) return result;
        _entries[command] = new(binding, id);
        return Success(command);
    }

    // Persistence must return false (or throw) without committing the caller's in-memory state.
    // Both IDs resolve during the save; old registration is released only after it succeeds.
    public async Task<ShortcutResult> ChangeAsync(ShortcutCommand command, ShortcutBinding binding, Func<Task<bool>> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        await _changes.WaitAsync();
        int? candidateId = null;
        try
        {
            if (_disposed) return Fail(command, "快捷键服务已关闭。");
            string error = Validate(command, binding);
            if (error.Length > 0) return Fail(command, error);
            _entries.TryGetValue(command, out var old);
            bool reuse = binding.Enabled && old?.Id is not null && old.Binding.Modifiers == binding.Modifiers && old.Binding.Key == binding.Key;
            if (binding.Enabled && !reuse)
            {
                var registration = Register(command, binding, out candidateId);
                if (!registration.Success) return registration;
            }
            bool saved;
            try { saved = await persist(); }
            catch (Exception ex) { return Fail(command, "保存快捷键失败：" + ex.Message); }
            if (!saved) return Fail(command, "保存快捷键失败，已保留原设置。");
            if (_disposed) return new(true, ""); // Closing already released all old and candidate IDs.
            if (old?.Id is int oldId && !reuse) Release(oldId);
            _entries[command] = new(binding, reuse ? old!.Id : candidateId);
            candidateId = null;
            return Success(command);
        }
        finally
        {
            if (candidateId is int abandoned) Release(abandoned);
            _changes.Release();
        }
    }

    private ShortcutResult Register(ShortcutCommand command, ShortcutBinding binding, out int? id)
    {
        id = null;
        int next = _nextId++;
        if (_nextId > 0xBFFF) _nextId = 0x5000;
        while (_commands.ContainsKey(next))
        {
            next = _nextId++;
            if (_nextId > 0xBFFF) _nextId = 0x5000;
        }
        try
        {
            if (!_register(next, binding.Modifiers | 0x4000, (uint)binding.Key))
                return Fail(command, "该组合键被系统或其他程序占用，原快捷键保持不变。");
        }
        catch (Exception ex) { return Fail(command, "注册快捷键失败：" + ex.Message); }
        _commands[next] = command;
        id = next;
        return new(true, "");
    }

    private void Release(int id)
    {
        if (!_commands.Remove(id)) return;
        _unregister(id);
    }

    private ShortcutResult Fail(ShortcutCommand command, string error)
    {
        _errors[command] = error;
        return new(false, error);
    }

    private ShortcutResult Success(ShortcutCommand command)
    {
        _errors.Remove(command);
        return new(true, "");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (int id in new List<int>(_commands.Keys)) Release(id);
        _entries.Clear();
    }
}
