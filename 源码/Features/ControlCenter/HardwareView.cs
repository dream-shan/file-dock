#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace FileDockDemo.Features.ControlCenter;

internal sealed class HardwareView : IDisposable
{
    private readonly HardwareController _controller;
    private readonly Border _homeCard;
    private readonly StackPanel _details = new();
    private readonly TextBlock _homeTitle = Ui.Text("正在识别硬件…", 12);
    private readonly TextBlock _homeSub = Ui.Text("", 11);
    private readonly TextBlock _summaryHeader = Ui.Text("", 11);
    private readonly StackPanel _profilePanel = new();
    private readonly StackPanel _telemetryPanel = new();
    private readonly StackPanel _capabilityPanel = new();
    private readonly TextBlock _error = Ui.Text("", 11);
    private bool _disposed;

    internal FrameworkElement Home => _homeCard;
    internal FrameworkElement Details => _details;
    internal event Action? DetailsRequested;

    internal HardwareView(HardwareController controller)
    {
        _controller = controller;

        var home = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var more = Ui.Button("硬件详情 ›", () => DetailsRequested?.Invoke());
        more.SetResourceReference(FrameworkElement.StyleProperty, "QuietButton");
        more.SetResourceReference(Control.ForegroundProperty, "Accent");
        more.FontSize = 11.5;
        more.Padding = new Thickness(6, 2, 6, 2);
        more.Margin = new Thickness(0);
        more.MinHeight = 24;
        AutomationProperties.SetName(more, "硬件与性能详情");
        DockPanel.SetDock(more, Dock.Right);
        header.Children.Add(more);

        var heading = Ui.Text("硬件与性能", 12.5);
        heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new Thickness(0);
        heading.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(heading);
        home.Children.Add(header);

        _homeTitle.Margin = new Thickness(2, 0, 0, 2);
        _homeTitle.TextWrapping = TextWrapping.NoWrap;
        _homeTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        home.Children.Add(_homeTitle);

        _homeSub.Margin = new Thickness(2, 0, 0, 0);
        _homeSub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _homeSub.TextWrapping = TextWrapping.Wrap;
        home.Children.Add(_homeSub);

        _homeCard = new Border
        {
            Child = home,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 8, 11, 9),
            Margin = new Thickness(0, 0, 0, 8)
        };
        _homeCard.SetResourceReference(Border.BackgroundProperty, "PaneCard");
        _homeCard.SetResourceReference(Border.BorderBrushProperty, "PaneOutline");

        // Details layout
        _summaryHeader.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _summaryHeader.Margin = new Thickness(0, 0, 0, 6);
        _details.Children.Add(_summaryHeader);

        _details.Children.Add(SectionCard("本机硬件档案", _profilePanel));
        _details.Children.Add(SectionCard("GPU 实时功耗与频率", _telemetryPanel));
        _details.Children.Add(SectionCard("性能参数支持矩阵", _capabilityPanel));

        _error.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _details.Children.Add(_error);

        var foot = Ui.Text("当前仅在普通权限下只读识别硬件与能力，不修改任何硬件设置；受支持的功耗、风扇与性能模式调节将由后续本地提权服务在 BIOS 安全上下限内提供。", 11);
        foot.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _details.Children.Add(foot);

        _controller.Changed += Render;
        Render(_controller.Current);
    }

    private static Border SectionCard(string title, Panel body)
    {
        var wrap = new StackPanel();
        var label = Ui.Text(title, 12.5);
        label.FontWeight = FontWeights.SemiBold;
        label.Margin = new Thickness(0, 0, 0, 6);
        wrap.Children.Add(label);
        wrap.Children.Add(body);
        var card = new Border
        {
            Child = wrap,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 8, 11, 9),
            Margin = new Thickness(0, 0, 0, 8)
        };
        card.SetResourceReference(Border.BackgroundProperty, "PaneCard");
        card.SetResourceReference(Border.BorderBrushProperty, "PaneOutline");
        return card;
    }

    private void Render(HardwareSnapshot snapshot)
    {
        if (_disposed) return;

        if (snapshot.Loading)
        {
            _homeTitle.Text = "正在识别本机硬件…";
            _homeSub.Text = "按需读取机型、GPU 与厂商能力接口";
            _summaryHeader.Text = "正在读取本机硬件信息…";
            _error.Text = "";
            return;
        }

        var id = snapshot.Identity;
        string modelLine = id.DisplayTitle + (id.FormFactor == HardwareFormFactor.Unknown ? "" : $" · {id.FormFactorLabel}");
        _homeTitle.Text = modelLine;
        _homeTitle.ToolTip = modelLine;

        string telemetrySummary = BuildCompactSummary(snapshot);
        _homeSub.Text = telemetrySummary;
        _homeSub.ToolTip = telemetrySummary;

        string stamp = snapshot.UpdatedAt is { } time ? $"最近读取：{time.ToLocalTime():HH:mm:ss}" : "尚未读取";
        string vendorNote = snapshot.VendorProbe.HasAnyVendorInterface
            ? $" · 已识别 {snapshot.VendorProbe.VendorFamily}"
            : " · 未检测到专用厂商控制接口";
        _summaryHeader.Text = stamp + vendorNote;

        RenderProfile(id);
        RenderTelemetry(snapshot.GpuTelemetry);
        RenderCapabilities(snapshot.Capabilities);
        _error.Text = snapshot.Error ?? "";
    }

    private static string BuildCompactSummary(HardwareSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Error))
            return snapshot.Error!;

        var parts = new System.Collections.Generic.List<string>(3);
        if (snapshot.GpuTelemetry is { } gpu)
        {
            if (gpu.PowerDrawWatts is { } draw)
            {
                string power = gpu.EnforcedLimitWatts is { } lim
                    ? $"GPU {draw.ToString("F1", CultureInfo.InvariantCulture)} / {lim.ToString("F0", CultureInfo.InvariantCulture)} W"
                    : $"GPU {draw.ToString("F1", CultureInfo.InvariantCulture)} W";
                parts.Add(power);
            }
            if (gpu.GraphicsClockMhz is { } clock)
                parts.Add($"{clock} MHz");
        }
        else if (snapshot.Identity.PhysicalGpus.Count > 0)
        {
            parts.Add(snapshot.Identity.PhysicalGpus[0].Name);
        }

        int elevatedCount = snapshot.Capabilities.Count(item => item.State == HardwareCapabilityState.RequiresElevatedService);
        int readOnlyCount = snapshot.Capabilities.Count(item => item.State == HardwareCapabilityState.AvailableReadOnly);
        if (elevatedCount > 0)
            parts.Add($"{elevatedCount} 项厂商/硬件调节接口（待提权服务）");
        else if (readOnlyCount > 0)
            parts.Add($"{readOnlyCount} 项只读监测");
        else
            parts.Add("只读硬件识别");

        return string.Join(" · ", parts);
    }

    private void RenderProfile(HardwareIdentity id)
    {
        _profilePanel.Children.Clear();
        AddInfoRow(_profilePanel, "机型", id.DisplayTitle + (id.Model.Length > 0 && !id.DisplayTitle.Contains(id.Model, StringComparison.OrdinalIgnoreCase) ? $" ({id.Model})" : ""));
        AddInfoRow(_profilePanel, "形态", id.FormFactorLabel);
        string board = string.Join(" ", new[] { id.BoardManufacturer, id.BoardProduct }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (id.BiosVersion.Length > 0) board = board.Length > 0 ? $"{board} · BIOS {id.BiosVersion}" : $"BIOS {id.BiosVersion}";
        AddInfoRow(_profilePanel, "主板与 BIOS", board.Length > 0 ? board : "未读出");

        string cpu = id.CpuName.Length > 0 ? id.CpuName : "未读出";
        if (id.CpuCores is { } cores && id.CpuThreads is { } threads)
            cpu += $"（{cores} 核 {threads} 线程）";
        AddInfoRow(_profilePanel, "处理器", cpu);

        if (id.PhysicalGpus.Count == 0)
        {
            AddInfoRow(_profilePanel, "物理显卡", "未检测到独立/集成物理显卡");
        }
        else
        {
            foreach (var gpu in id.PhysicalGpus)
            {
                string detail = string.IsNullOrWhiteSpace(gpu.DriverVersion) ? gpu.Name : $"{gpu.Name}（驱动 {gpu.DriverVersion}）";
                AddInfoRow(_profilePanel, "物理显卡", detail);
            }
        }
        if (id.FilteredVirtualGpuCount > 0)
        {
            var note = Ui.Text($"已自动过滤 {id.FilteredVirtualGpuCount} 个虚拟显示适配器。", 11);
            note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            note.Margin = new Thickness(0, 2, 0, 0);
            _profilePanel.Children.Add(note);
        }
    }

    private void RenderTelemetry(GpuPowerTelemetry? gpu)
    {
        _telemetryPanel.Children.Clear();
        if (gpu == null)
        {
            var note = Ui.Text("未检测到支持 NVML 功耗与频率只读监测的 NVIDIA 显卡。", 11.5);
            note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _telemetryPanel.Children.Add(note);
            return;
        }

        AddInfoRow(_telemetryPanel, "显卡", gpu.GpuName);
        if (gpu.PowerDrawWatts is { } draw)
            AddInfoRow(_telemetryPanel, "当前功耗", $"{draw.ToString("F2", CultureInfo.InvariantCulture)} W");
        if (gpu.EnforcedLimitWatts is { } enforced)
            AddInfoRow(_telemetryPanel, "当前生效上限", $"{enforced.ToString("F1", CultureInfo.InvariantCulture)} W");
        if (gpu.DefaultLimitWatts is { } def)
            AddInfoRow(_telemetryPanel, "默认功耗上限", $"{def.ToString("F0", CultureInfo.InvariantCulture)} W");
        if (gpu.MinLimitWatts is { } min && gpu.MaxLimitWatts is { } max)
            AddInfoRow(_telemetryPanel, "硬件功耗范围", $"{min.ToString("F0", CultureInfo.InvariantCulture)} – {max.ToString("F0", CultureInfo.InvariantCulture)} W");
        if (gpu.GraphicsClockMhz is { } gr)
            AddInfoRow(_telemetryPanel, "核心频率", gpu.MaxGraphicsClockMhz is { } maxGr ? $"{gr} MHz（最高 {maxGr} MHz）" : $"{gr} MHz");
        if (gpu.MemoryClockMhz is { } mem)
            AddInfoRow(_telemetryPanel, "显存频率", gpu.MaxMemoryClockMhz is { } maxMem ? $"{mem} MHz（最高 {maxMem} MHz）" : $"{mem} MHz");
        if (gpu.TemperatureCelsius is { } temp)
            AddInfoRow(_telemetryPanel, "GPU 温度", $"{temp} °C");
        if (gpu.FanPercent is { } fan)
            AddInfoRow(_telemetryPanel, "GPU 风扇", $"{fan}%");
        if (gpu.PerformanceState is { } ps)
            AddInfoRow(_telemetryPanel, "性能状态", $"P{ps}");
    }

    private void RenderCapabilities(System.Collections.Generic.IReadOnlyList<HardwareCapabilityItem> items)
    {
        _capabilityPanel.Children.Clear();
        if (items.Count == 0)
        {
            var empty = Ui.Text("暂无能力探测结果，请点击顶部刷新。", 11.5);
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _capabilityPanel.Children.Add(empty);
            return;
        }

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var row = new StackPanel { Margin = new Thickness(0, i == 0 ? 0 : 6, 0, 0) };
            var head = new DockPanel();
            var badge = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var badgeText = Ui.Text(HardwareCapabilityEvaluator.StateLabel(item.State), 10.5);
            badgeText.Margin = new Thickness(0);
            badgeText.FontWeight = FontWeights.SemiBold;
            badge.Child = badgeText;
            badge.SetResourceReference(Border.BackgroundProperty,
                item.State == HardwareCapabilityState.RequiresElevatedService ? "Accent" : "SliderTrack");
            badgeText.SetResourceReference(TextBlock.ForegroundProperty,
                item.State == HardwareCapabilityState.RequiresElevatedService ? "OnAccent" : "Ink");
            DockPanel.SetDock(badge, Dock.Right);
            head.Children.Add(badge);

            var title = Ui.Text(item.Title, 12);
            title.FontWeight = FontWeights.SemiBold;
            title.Margin = new Thickness(0);
            title.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(title);
            row.Children.Add(head);

            var summary = Ui.Text(item.Summary, 11.5);
            summary.Margin = new Thickness(0, 2, 0, 1);
            row.Children.Add(summary);

            var detail = Ui.Text(item.Detail, 11);
            detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            detail.Margin = new Thickness(0);
            row.Children.Add(detail);

            if (i > 0)
            {
                var divider = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 0) };
                divider.SetResourceReference(Border.BackgroundProperty, "PaneOutline");
                _capabilityPanel.Children.Add(divider);
            }
            _capabilityPanel.Children.Add(row);
        }
    }

    private static void AddInfoRow(Panel parent, string label, string value)
    {
        var text = Ui.Text($"{label}：{value}", 11.5);
        text.Margin = new Thickness(0, 0, 0, 3);
        parent.Children.Add(text);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controller.Changed -= Render;
        _controller.Dispose();
    }
}
