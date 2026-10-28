using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FileDockDemo;

public sealed class StorageService
{
    private const int CurrentVersion = 1;
    private readonly string _directory;
    private readonly string _statePath;
    private readonly string _backupPath;
    private readonly object _sync = new();
    private bool _preserveBackup;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public StorageService(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _directory = System.IO.Path.GetFullPath(dataDirectory);
        _statePath = System.IO.Path.Combine(_directory, "state.json");
        _backupPath = System.IO.Path.Combine(_directory, "state.backup.json");
    }

    public AppState Load()
    {
        lock (_sync)
        {
            if (!File.Exists(_statePath))
            {
                if (!File.Exists(_backupPath))
                    return new AppState();
                var recovered = Read(_backupPath);
                _preserveBackup = true;
                return recovered;
            }

            try
            {
                var state = Read(_statePath);
                _preserveBackup = false;
                return state;
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or IOException)
            {
                if (!File.Exists(_backupPath))
                    throw;
                // A corrupt main file must not discard the last known valid state.
                var recovered = Read(_backupPath);
                _preserveBackup = true;
                return recovered;
            }
        }
    }

    public void Save(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state);
        lock (_sync)
        {
            Directory.CreateDirectory(_directory);
            var temporaryPath = System.IO.Path.Combine(_directory, "state." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, new StateEnvelope { Version = CurrentVersion, State = state }, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(_statePath))
                    File.Replace(temporaryPath, _statePath, _preserveBackup ? null : _backupPath, ignoreMetadataErrors: true);
                else
                    File.Move(temporaryPath, _statePath);
                _preserveBackup = false;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
    }

    private static AppState Read(string path)
    {
        using var stream = File.OpenRead(path);
        var envelope = JsonSerializer.Deserialize<StateEnvelope>(stream, JsonOptions)
                       ?? throw new InvalidDataException("配置文件为空。");
        if (envelope.Version > CurrentVersion)
            throw new NotSupportedException($"配置版本 {envelope.Version} 比当前程序支持的版本新。请使用较新版本的文件坞；原配置和备份均已保留。");
        if (envelope.Version != CurrentVersion)
            throw new InvalidDataException($"无法读取版本为 {envelope.Version} 的配置文件。");
        var state = envelope.State ?? throw new InvalidDataException("配置文件缺少应用状态。");
        Validate(state);
        return state;
    }

    private static void Validate(AppState state)
    {
        if (state.Pins is null)
            throw new InvalidDataException("配置文件中的列表无效。");
        if (state.Pins.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.Path) ||
                                    string.IsNullOrWhiteSpace(entry.Id) || entry.DisplayName is null))
            throw new InvalidDataException("配置文件中的文件记录无效。");
        if ((state.PanelLeft.HasValue != state.PanelTop.HasValue) || (state.PanelLeft.HasValue && (!double.IsFinite(state.PanelLeft.Value) || !double.IsFinite(state.PanelTop!.Value)))) throw new InvalidDataException("窗口展开位置无效。");
        if (state.DockSide is not ("Left" or "Right") || !double.IsFinite(state.DockTop))
            throw new InvalidDataException("配置文件中的窗口位置无效。");
    }

    public sealed class StateEnvelope
    {
        public int Version { get; set; }
        public AppState? State { get; set; }
    }
}

public static class FileService
{
    public static void Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // Do not resolve user-added shortcuts: the shell must preserve arguments and working directory.
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void Reveal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        if (fullPath.Contains('"'))
            throw new ArgumentException("文件路径中不能包含双引号。", nameof(path));
        using var process = Process.Start(new ProcessStartInfo("explorer.exe")
        {
            Arguments = "/select,\"" + fullPath + "\"",
            UseShellExecute = true
        });
    }

    public static string NormalizeKey(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path))
            .ToUpperInvariant();
    }
}
