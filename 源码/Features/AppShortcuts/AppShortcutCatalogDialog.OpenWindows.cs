#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace FileDockDemo;

internal sealed partial class AppShortcutCatalogDialog
{
    private readonly Func<CancellationToken, Task<OpenWindowMenuScanResult>>? _scanOpenWindows;
    private readonly Dictionary<string, OpenWindowMenuApp> _liveWindowMenus = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _openWindowsHint = Ui.Text("", 12);
    private CancellationTokenSource? _openWindowsCancellation;
    private Button? _refreshOpenWindowsButton;
    private Button? _liveFilterButton;
    private bool _scanningOpenWindows;
    private bool _onlyLiveWindowShortcuts;
    private int _openWindowsGeneration;

    private void SetOpenWindowsHint(string text)
    {
        _openWindowsHint.Text = text;
        _openWindowsHint.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AddOpenWindowCatalogControls(WrapPanel softwareActions)
    {
        if (_scanOpenWindows == null) return;

        _refreshOpenWindowsButton = CompactButton(_scanningOpenWindows ? "读取打开窗口中…" : "刷新打开窗口", () => {
            if (_closed || _busy || _scanningOpenWindows) return;
            _ = ScanOpenWindowsAsync();
        });
        _refreshOpenWindowsButton.IsEnabled = !_scanningOpenWindows;
        AutomationProperties.SetName(_refreshOpenWindowsButton, "刷新打开窗口");
        softwareActions.Children.Add(_refreshOpenWindowsButton);

        _liveFilterButton = CompactButton(LiveFilterLabel(), () => {
            if (_closed || _busy) return;
            _onlyLiveWindowShortcuts = !_onlyLiveWindowShortcuts;
            _page = 0;
            UpdateOpenWindowsButtons();
            RenderResults();
        });
        AutomationProperties.SetName(_liveFilterButton, "仅看当前窗口实读");
        ApplyLiveFilterStyle();
        softwareActions.Children.Add(_liveFilterButton);
    }

    private string LiveFilterLabel() =>
        _onlyLiveWindowShortcuts ? "仅看当前窗口实读 · 已开启" : "仅看当前窗口实读";

    private void ApplyLiveFilterStyle()
    {
        if (_liveFilterButton == null) return;
        if (_onlyLiveWindowShortcuts)
        {
            _liveFilterButton.SetResourceReference(Control.BackgroundProperty, "Accent");
            _liveFilterButton.SetResourceReference(Control.ForegroundProperty, "OnAccent");
            _liveFilterButton.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            _liveFilterButton.ClearValue(Control.BackgroundProperty);
            _liveFilterButton.ClearValue(Control.ForegroundProperty);
            _liveFilterButton.FontWeight = FontWeights.Normal;
        }
    }

    private void UpdateOpenWindowsButtons()
    {
        if (_refreshOpenWindowsButton != null)
        {
            _refreshOpenWindowsButton.Content = _scanningOpenWindows ? "读取打开窗口中…" : "刷新打开窗口";
            _refreshOpenWindowsButton.IsEnabled = !_scanningOpenWindows;
        }
        if (_liveFilterButton != null)
        {
            _liveFilterButton.Content = LiveFilterLabel();
            ApplyLiveFilterStyle();
        }
    }

    private async Task ScanOpenWindowsAsync()
    {
        if (_scanOpenWindows == null || _closed) return;

        _openWindowsCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _openWindowsCancellation = cancellation;
        int generation = ++_openWindowsGeneration;
        _scanningOpenWindows = true;
        UpdateOpenWindowsButtons();
        SetOpenWindowsHint("正在后台读取当前已打开窗口的菜单快捷键…");

        try
        {
            var result = await _scanOpenWindows(cancellation.Token);
            if (_closed || generation != _openWindowsGeneration) return;

            _liveWindowMenus.Clear();
            foreach (var app in result.Apps)
            {
                if (app == null || app.Rules.Count == 0) continue;
                _liveWindowMenus[app.IdentityKey] = app;
            }

            int totalRules = _liveWindowMenus.Values.Sum(app => app.Rules.Count);
            string summary;
            if (totalRules > 0)
            {
                summary = $"已从 {result.MatchedWindows} 个打开窗口读到 {totalRules} 条菜单快捷键（仅本次查看，不写入配置）。";
                if (result.TimedOutPartial) summary += "部分窗口响应较慢已跳过。";
            }
            else if (result.ScannedWindows > 0)
            {
                summary = $"已检查 {result.ScannedWindows} 个打开窗口，暂未读到公开的菜单快捷键。";
            }
            else
            {
                summary = "当前未发现可读菜单的已打开应用窗口。";
            }
            SetOpenWindowsHint(summary);

            if (_catalogVisible)
            {
                RefreshSoftwareChoices();
                RenderResults();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_closed && generation == _openWindowsGeneration)
            {
                bool hasPrevious = _liveWindowMenus.Count > 0;
                SetOpenWindowsHint(hasPrevious
                    ? "刷新当前打开窗口菜单失败，已保留本次上次读取结果。可重试刷新或手动单窗读取。"
                    : "暂时无法批量读取当前打开窗口菜单。可点击刷新或使用「读取窗口菜单」单窗读取。");
            }
        }
        finally
        {
            if (generation == _openWindowsGeneration)
            {
                _scanningOpenWindows = false;
                if (ReferenceEquals(_openWindowsCancellation, cancellation))
                    _openWindowsCancellation = null;
                if (!_closed) UpdateOpenWindowsButtons();
            }
        }
    }

    private IEnumerable<ImportedAppShortcut> LiveRulesFor(SoftwareChoice choice, bool deduplicateWithSaved)
    {
        var seen = new HashSet<(string Command, string Key)>();

        if (choice.PackageOnly && _packageMenus.TryGetValue(choice.Id!, out var manualPackage))
        {
            foreach (var rule in manualPackage.Rules)
            {
                if (seen.Add((rule.Command.ToLowerInvariant(), rule.Key.Replace(" +", "+").Replace("+ ", "+").ToLowerInvariant())))
                    yield return rule;
            }
        }

        if (_liveWindowMenus.TryGetValue(choice.Identity, out var live))
        {
            var savedMenu = deduplicateWithSaved ? choice.Group?.MenuSource?.Rules : null;
            foreach (var rule in live.Rules)
            {
                if (savedMenu != null && OpenWindowMenuScanner.ContainsRule(savedMenu, rule)) continue;
                if (seen.Add((rule.Command.ToLowerInvariant(), rule.Key.Replace(" +", "+").Replace("+ ", "+").ToLowerInvariant())))
                    yield return rule;
            }
        }
    }
}
