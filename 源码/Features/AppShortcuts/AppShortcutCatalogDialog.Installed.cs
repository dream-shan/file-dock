using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace FileDockDemo;

internal sealed partial class AppShortcutCatalogDialog
{
    private sealed record SoftwareChoice(string Name, string? Path, string? Id = null,
        string? InstalledName = null, AppShortcutGroup? Group = null, string? PinnedName = null)
    {
        internal string Identity => Path == null ? "id:" + Id : FileService.NormalizeKey(Path);
        internal bool PackageOnly => Path == null && Id?.Contains('!') == true;
    }

    private IReadOnlyList<DiscoveredApp> _installed = [];
    private IReadOnlyList<SoftwareChoice> _choices = [];
    private readonly Dictionary<string,(string Name, List<ImportedAppShortcut> Rules)> _packageMenus = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _discoveryHint = Ui.Text("", 12);
    private CancellationTokenSource? _discoveryCancellation;
    private SoftwareChoice? _selectedSoftware;
    private bool _catalogVisible;
    private bool SelectedExecutable => string.Equals(Path.GetExtension(_selectedSoftware?.Path), ".exe",
        StringComparison.OrdinalIgnoreCase);

    private void SetDiscoveryHint(string text)
    {
        _discoveryHint.Text = text;
        _discoveryHint.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task LoadInstalledAsync(Func<CancellationToken,Task<IReadOnlyList<DiscoveredApp>>> discover)
    {
        using var cancellation = new CancellationTokenSource();
        _discoveryCancellation = cancellation;
        try
        {
            var installed = await discover(cancellation.Token);
            if (_closed) return;
            _installed = installed;
            SetDiscoveryHint(installed.Count == 0 ? "Windows 应用列表为空。可直接选择程序或窗口。" : "");
            if (_catalogVisible)
            {
                RefreshSoftwareChoices();
                RenderResults();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_closed) SetDiscoveryHint("暂时无法读取 Windows 应用列表。可搜索已收录的软件，或直接选择程序。");
        }
        finally { _discoveryCancellation = null; }
    }

    private void RefreshSoftwareChoices()
    {
        var byIdentity = new Dictionary<string,SoftwareChoice>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in _getGroups())
        {
            string? path = group.HasPackageIdentity ? null : group.Path;
            string? id = group.HasPackageIdentity ? group.AppId : null;
            byIdentity[group.IdentityKey] = new(group.Name, path, id, Group: group);
        }
        foreach (var fixedApp in _getFixed())
        {
            string key = FileService.NormalizeKey(fixedApp.Path);
            if (byIdentity.TryGetValue(key, out var registered))
                byIdentity[key] = registered with { PinnedName = fixedApp.DisplayName };
            else byIdentity[key] = new(fixedApp.DisplayName, fixedApp.Path, PinnedName: fixedApp.DisplayName);
        }
        if (!_allSoftware && _group != null)
        {
            string key = _group.IdentityKey;
            byIdentity.TryGetValue(key, out var existing);
            string? path = _group.HasPackageIdentity ? null : _group.Path;
            string? id = _group.HasPackageIdentity ? _group.AppId : existing?.Id;
            byIdentity[key] = new(_group.Name, path, id, existing?.InstalledName, Group: _group, PinnedName: existing?.PinnedName);
        }

        foreach (var app in _installed)
        {
            var entry = new SoftwareChoice(app.Name, app.Path, app.Id, app.Name);
            if (byIdentity.TryGetValue(entry.Identity, out var existing))
                byIdentity[entry.Identity] = existing with { Id = app.Id, InstalledName = app.Name };
            else byIdentity.Add(entry.Identity, entry);
        }
        foreach (var (id, menu) in _packageMenus)
        {
            var entry = new SoftwareChoice(menu.Name, null, id);
            byIdentity.TryAdd(entry.Identity, entry);
        }
        foreach (var live in _liveWindowMenus.Values)
        {
            string? path = live.HasPackageIdentity ? null : live.Path;
            string? id = live.HasPackageIdentity ? live.PackageId : null;
            var entry = new SoftwareChoice(live.Name, path, id);
            byIdentity.TryAdd(entry.Identity, entry);
        }
        _choices = byIdentity.Values.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Identity, StringComparer.OrdinalIgnoreCase).ToList();
        _changingSoftware = true;
        try
        {
            _software.Items.Clear();
            var all = new ComboBoxItem { Content = "所有软件" };
            _software.Items.Add(all);
            if (_allSoftware) _software.SelectedItem = all;
            foreach (var choice in _choices)
            {
                string kind = choice.Path == null ? "应用入口" :
                    Path.GetExtension(choice.Path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? "快捷方式" : "程序";
                var item = new ComboBoxItem { Content = choice.Name + " · " + kind, ToolTip = choice.Path ?? choice.Id, Tag = choice };
                _software.Items.Add(item);
                if (!_allSoftware && string.Equals(choice.Identity, _selectedSoftware?.Identity, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedSoftware = choice;
                    _software.SelectedItem = item;
                }
            }
        }
        finally { _changingSoftware = false; }
    }
}
