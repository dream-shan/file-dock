#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace FileDockDemo.Features.ControlCenter;

internal sealed class HardwareView : IDisposable
{
    private const string InstallServiceCommand = @".\TemperatureService\Install.ps1 -SourceDirectory .\TemperatureService";

    private readonly HardwareController _controller;
    private readonly Func<IReadOnlyList<HardwarePerformancePreset>> _getPresets;
    private readonly Func<IReadOnlyList<HardwarePerformancePreset>, Task<bool>> _savePresets;
    private readonly Border _homeCard;
    private readonly Border _homeModeStrip;
    private readonly StackPanel _details = new();
    private readonly TextBlock _homeTitle = Ui.Text("正在识别硬件…", 12);
    private readonly TextBlock _homeSub = Ui.Text("", 11);
    private readonly TextBlock _summaryHeader = Ui.Text("", 11);
    private readonly Border _controlCard;
    private readonly StackPanel _controlBody = new();
    private readonly Border _tuningCard;
    private readonly StackPanel _tuningBody = new();
    private readonly Border _presetsCard;
    private readonly StackPanel _presetsBody = new();
    private readonly StackPanel _profilePanel = new();
    private readonly StackPanel _telemetryPanel = new();
    private readonly StackPanel _capabilityPanel = new();
    private readonly TextBlock _actionFeedback = Ui.Text("", 11);
    private readonly TextBlock _error = Ui.Text("", 11);
    private readonly Dictionary<int, Button> _homeModes = new();
    private readonly Dictionary<int, Button> _detailModes = new();
    private readonly Dictionary<uint, (Slider Slider, TextBlock ValueText, HardwareControlFeature Feature)> _featureSliders = new();
    private readonly TextBox _presetNameBox = new() { MinHeight = 30, MaxLength = 60, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 0, 6, 0) };
    private readonly StackPanel _presetListPanel = new();
    private Button? _fullSpeedButton;
    private Button? _applyCustomButton;
    private TextBlock? _fanRpmText;
    private TextBlock? _customHintText;
    private string _featureSignature = "";
    private bool _binding;
    private bool _disposed;

    internal FrameworkElement Home => _homeCard;
    internal FrameworkElement Details => _details;
    internal event Action? DetailsRequested;

    internal HardwareView(
        HardwareController controller,
        Func<IReadOnlyList<HardwarePerformancePreset>>? getPresets = null,
        Func<IReadOnlyList<HardwarePerformancePreset>, Task<bool>>? savePresets = null)
    {
        _controller = controller;
        _getPresets = getPresets ?? (() => []);
        _savePresets = savePresets ?? (_ => Task.FromResult(false));

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

        _homeModeStrip = BuildModeStrip(_homeModes, "主页硬件模式");
        _homeModeStrip.Visibility = Visibility.Collapsed;
        home.Children.Add(_homeModeStrip);

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

        _actionFeedback.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        _actionFeedback.Margin = new Thickness(0, 0, 0, 6);
        _actionFeedback.Visibility = Visibility.Collapsed;
        _details.Children.Add(_actionFeedback);

        _controlCard = SectionCard("厂商性能模式与散热", _controlBody);
        _controlCard.Visibility = Visibility.Collapsed;
        _details.Children.Add(_controlCard);

        _tuningCard = SectionCard("自定义功耗与温控上限", _tuningBody);
        _tuningCard.Visibility = Visibility.Collapsed;
        _details.Children.Add(_tuningCard);

        BuildPresetsSection();
        _presetsCard = SectionCard("已保存的性能方案", _presetsBody);
        _presetsCard.Visibility = Visibility.Collapsed;
        _details.Children.Add(_presetsCard);

        _details.Children.Add(SectionCard("本机硬件档案", _profilePanel));
        _details.Children.Add(SectionCard("GPU 实时功耗与频率", _telemetryPanel));
        _details.Children.Add(SectionCard("性能参数支持矩阵", _capabilityPanel));

        _error.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _details.Children.Add(_error);

        var foot = Ui.Text("主程序保持普通权限；硬件写入经由本机提权服务按 BIOS 安全上下限二次校验，启动时不自动重放硬件写入。", 11);
        foot.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _details.Children.Add(foot);

        _controller.Changed += Render;
        Render(_controller.Current);
    }

    private Border BuildModeStrip(Dictionary<int, Button> map, string automationPrefix)
    {
        var choices = new UniformGrid { Columns = 4 };
        foreach (int mode in new[] { HardwareFeatureCatalog.ModeQuiet, HardwareFeatureCatalog.ModeBalance,
            HardwareFeatureCatalog.ModePerformance, HardwareFeatureCatalog.ModeCustom })
        {
            string label = HardwareFeatureCatalog.ModeLabel(mode);
            int targetMode = mode;
            var button = Ui.Button(label, () => {
                SetFeedback($"正在切换到{label}模式…");
                _controller.SetThermalMode(targetMode);
            });
            button.SetResourceReference(FrameworkElement.StyleProperty, "QuietButton");
            button.SetResourceReference(Control.ForegroundProperty, "Ink");
            button.Margin = new Thickness(1, 0, 1, 0);
            button.Padding = new Thickness(3, 5, 3, 5);
            button.MinHeight = 28;
            button.FontSize = 11.5;
            AutomationProperties.SetName(button, $"{automationPrefix}：{label}");
            map[mode] = button;
            choices.Children.Add(button);
        }
        var strip = new Border
        {
            Child = choices,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(2.5),
            Margin = new Thickness(0, 0, 0, 6)
        };
        strip.SetResourceReference(Border.BackgroundProperty, "SliderTrack");
        return strip;
    }

    private void BuildPresetsSection()
    {
        _presetNameBox.SetResourceReference(Control.BackgroundProperty, "ContentSurface");
        _presetNameBox.SetResourceReference(Control.ForegroundProperty, "Ink");
        _presetNameBox.SetResourceReference(Control.BorderBrushProperty, "Border");
        AutomationProperties.SetName(_presetNameBox, "性能方案名称");

        var addRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var saveBtn = Ui.Button("保存当前方案", async () => await SaveCurrentPresetAsync());
        saveBtn.Padding = new Thickness(8, 4, 8, 4);
        saveBtn.Margin = new Thickness(0);
        saveBtn.MinHeight = 30;
        AutomationProperties.SetName(saveBtn, "保存当前硬件方案");
        DockPanel.SetDock(saveBtn, Dock.Right);
        addRow.Children.Add(saveBtn);
        addRow.Children.Add(_presetNameBox);
        _presetsBody.Children.Add(addRow);
        _presetsBody.Children.Add(_presetListPanel);
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

    private void SetFeedback(string message)
    {
        _actionFeedback.Text = message;
        _actionFeedback.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Render(HardwareSnapshot snapshot)
    {
        if (_disposed) return;

        if (snapshot.Loading)
        {
            _homeModeStrip.Visibility = Visibility.Collapsed;
            _homeTitle.Text = "正在识别本机硬件…";
            _homeSub.Text = "按需读取机型、GPU 与厂商能力接口";
            _summaryHeader.Text = "正在读取本机硬件信息…";
            _error.Text = "";
            return;
        }

        var id = snapshot.Identity;
        var control = snapshot.Control;
        bool ready = control?.Ready == true;

        _homeModeStrip.Visibility = ready && control!.ThermalMode != null ? Visibility.Visible : Visibility.Collapsed;
        if (ready) UpdateModeButtons(_homeModes, control!);

        string modelLine = id.DisplayTitle + (id.FormFactor == HardwareFormFactor.Unknown ? "" : $" · {id.FormFactorLabel}");
        _homeTitle.Text = modelLine;
        _homeTitle.ToolTip = modelLine;

        string telemetrySummary = BuildCompactSummary(snapshot);
        _homeSub.Text = telemetrySummary;
        _homeSub.ToolTip = telemetrySummary;

        string stamp = snapshot.UpdatedAt is { } time ? $"最近读取：{time.ToLocalTime():HH:mm:ss}" : "尚未读取";
        string vendorNote = snapshot.VendorProbe.HasAnyVendorInterface
            ? (ready ? $" · 已连接提权服务 ({control!.VendorFamily})" : $" · 已识别 {snapshot.VendorProbe.VendorFamily}")
            : " · 未检测到专用厂商控制接口";
        _summaryHeader.Text = stamp + vendorNote;

        if (!string.IsNullOrWhiteSpace(control?.Message) && string.IsNullOrWhiteSpace(snapshot.Error))
            SetFeedback(control.Message!);
        else if (!string.IsNullOrWhiteSpace(snapshot.Error))
            SetFeedback("");

        RenderControlSection(snapshot);
        RenderTuningSection(control);
        RenderPresetsSection(control);
        RenderProfile(id);
        RenderTelemetry(snapshot.GpuTelemetry);
        RenderCapabilities(snapshot.Capabilities);
        _error.Text = snapshot.Error ?? "";
    }

    private void RenderControlSection(HardwareSnapshot snapshot)
    {
        var control = snapshot.Control;
        if (!snapshot.VendorProbe.HasAnyVendorInterface && control?.Ready != true)
        {
            _controlCard.Visibility = Visibility.Collapsed;
            return;
        }
        _controlCard.Visibility = Visibility.Visible;

        if (control?.Ready == true)
        {
            if (_detailModes.Count == 0 || _controlBody.Children.Count == 0 || _fanRpmText == null)
            {
                _controlBody.Children.Clear();
                _detailModes.Clear();
                _controlBody.Children.Add(BuildModeStrip(_detailModes, "硬件性能模式"));

                _fanRpmText = Ui.Text("", 11.5);
                _fanRpmText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                _fanRpmText.Margin = new Thickness(0, 0, 0, 6);
                _controlBody.Children.Add(_fanRpmText);

                var row = new WrapPanel();
                _fullSpeedButton = Ui.Button("全速散热", () => {
                    bool next = !(_controller.Current.Control?.FanFullSpeed == true);
                    SetFeedback(next ? "正在开启全速散热…" : "正在关闭全速散热…");
                    _controller.SetFeature(HardwareFeatureCatalog.FeatureFanFullSpeed, next ? 1 : 0);
                });
                _fullSpeedButton.Margin = new Thickness(0, 0, 6, 4);
                _fullSpeedButton.Padding = new Thickness(10, 4, 10, 4);
                AutomationProperties.SetName(_fullSpeedButton, "切换全速散热");
                row.Children.Add(_fullSpeedButton);

                var resetBtn = Ui.Button("恢复 BIOS 默认", () => {
                    SetFeedback("正在恢复 BIOS 默认参数并切回均衡模式…");
                    _controller.ResetDefaults();
                });
                resetBtn.Margin = new Thickness(0, 0, 0, 4);
                resetBtn.Padding = new Thickness(10, 4, 10, 4);
                AutomationProperties.SetName(resetBtn, "恢复 BIOS 默认");
                row.Children.Add(resetBtn);
                _controlBody.Children.Add(row);
            }

            UpdateModeButtons(_detailModes, control);
            string rpm = HardwareCapabilityEvaluator.FormatFanRpm(control);
            string maxNote = control.FanMaxRpm is { } max ? $" · 参考上限 {max} RPM" : "";
            string acNote = control.OnAcPower ? "交流供电" : "电池供电（野兽/自定义已锁定）";
            _fanRpmText!.Text = (rpm.Length > 0 ? rpm + maxNote + " · " : "") + acNote;

            if (_fullSpeedButton != null)
            {
                _fullSpeedButton.Visibility = control.SupportsFanFullSpeed ? Visibility.Visible : Visibility.Collapsed;
                bool active = control.FanFullSpeed == true;
                _fullSpeedButton.Content = active ? "全速散热 · 已开启" : "全速散热 · 关闭";
                _fullSpeedButton.SetResourceReference(Control.BackgroundProperty, active ? "Accent" : "ContentSurface");
                _fullSpeedButton.SetResourceReference(Control.ForegroundProperty, active ? "OnAccent" : "Ink");
            }
            return;
        }

        // Vendor interface exists, but local elevated service is missing or outdated.
        _detailModes.Clear();
        _fanRpmText = null;
        _fullSpeedButton = null;
        _controlBody.Children.Clear();
        _controlBody.Children.Add(Ui.Text(
            control?.Message ?? "本机提权服务未安装或尚未更新到 6.2。主程序保持普通权限，请以管理员身份运行安装脚本更新服务：", 11.5));
        var cmdText = Ui.Text(InstallServiceCommand, 11);
        cmdText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _controlBody.Children.Add(cmdText);
        var copyBtn = Ui.Button("复制管理员更新命令", () => {
            try { Clipboard.SetText(InstallServiceCommand); SetFeedback("已复制服务更新命令。请在管理员 PowerShell 中运行后点击刷新。"); }
            catch (Exception ex) { SetFeedback("复制失败：" + ex.Message); }
        });
        copyBtn.Padding = new Thickness(10, 4, 10, 4);
        copyBtn.Margin = new Thickness(0, 4, 0, 0);
        _controlBody.Children.Add(copyBtn);
    }

    private static void UpdateModeButtons(Dictionary<int, Button> map, HardwareControlSnapshot control)
    {
        foreach (var pair in map)
        {
            int mode = pair.Key;
            bool active = control.ThermalMode == mode;
            bool needsAc = mode is HardwareFeatureCatalog.ModePerformance or HardwareFeatureCatalog.ModeCustom;
            bool supported = mode != HardwareFeatureCatalog.ModeCustom || control.SupportsCustomMode;
            bool enabled = supported && (!needsAc || control.OnAcPower);
            pair.Value.IsEnabled = enabled;
            pair.Value.SetResourceReference(Control.BackgroundProperty, active ? "Accent" : "Transparent");
            pair.Value.SetResourceReference(Control.ForegroundProperty, active ? "OnAccent" : "Ink");
            pair.Value.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            pair.Value.ToolTip = !supported ? "当前机型 BIOS 未提供自定义参数表" :
                needsAc && !control.OnAcPower ? "电池供电下不可切换到野兽或自定义模式" :
                $"切换至 {HardwareFeatureCatalog.ModeLabel(mode)}模式";
        }
    }

    private void RenderTuningSection(HardwareControlSnapshot? control)
    {
        if (control?.Ready != true || control.Features.Count == 0)
        {
            _tuningCard.Visibility = Visibility.Collapsed;
            return;
        }
        _tuningCard.Visibility = Visibility.Visible;

        string signature = string.Join("|", control.Features.Select(f => $"{f.HexId}:{f.Min}:{f.Max}:{f.Step}"));
        if (signature != _featureSignature)
        {
            _featureSignature = signature;
            _tuningBody.Children.Clear();
            _featureSliders.Clear();

            _customHintText = Ui.Text("", 11);
            _customHintText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _customHintText.Margin = new Thickness(0, 0, 0, 6);
            _tuningBody.Children.Add(_customHintText);

            foreach (var feature in control.Features)
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                var top = new DockPanel();
                var valueText = Ui.Text("", 11.5);
                valueText.FontWeight = FontWeights.SemiBold;
                valueText.Margin = new Thickness(6, 0, 0, 0);
                DockPanel.SetDock(valueText, Dock.Right);
                top.Children.Add(valueText);

                var title = Ui.Text(feature.Name, 11.5);
                title.Margin = new Thickness(0);
                top.Children.Add(title);
                row.Children.Add(top);

                var range = Ui.Text($"BIOS 安全范围：{feature.Min}–{feature.Max} {feature.Unit} · 步长 {feature.Step} · 默认 {feature.DefaultValue} {feature.Unit}", 10.5);
                range.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                range.Margin = new Thickness(0, 1, 0, 3);
                row.Children.Add(range);

                var slider = new Slider
                {
                    Minimum = feature.Min,
                    Maximum = feature.Max,
                    TickFrequency = feature.Step,
                    IsSnapToTickEnabled = true,
                    SmallChange = feature.Step,
                    LargeChange = feature.Step * 2
                };
                slider.SetResourceReference(FrameworkElement.StyleProperty, "AppleCapsuleSlider");
                AutomationProperties.SetName(slider, feature.Name);
                var captured = feature;
                slider.ValueChanged += (_, _) => {
                    if (_binding) return;
                    valueText.Text = $"{(int)Math.Round(slider.Value)} {captured.Unit}";
                };
                row.Children.Add(slider);

                _featureSliders[feature.Id] = (slider, valueText, feature);
                _tuningBody.Children.Add(row);
            }

            _applyCustomButton = Ui.Button("应用自定义参数", ApplySliderValues);
            _applyCustomButton.Padding = new Thickness(12, 5, 12, 5);
            AutomationProperties.SetName(_applyCustomButton, "应用自定义硬件参数");
            _tuningBody.Children.Add(_applyCustomButton);
        }

        bool editable = control.ThermalMode == HardwareFeatureCatalog.ModeCustom && control.OnAcPower;
        if (_customHintText != null)
        {
            _customHintText.Text = !control.OnAcPower
                ? "电池供电下已锁定自定义功耗与温控上限，请接通交流电源。"
                : editable
                    ? "拖动滑块设定目标值后点击「应用自定义参数」写入（严格按 BIOS 范围校验）。"
                    : "当前非「自定义」模式；请先在上方切换至「自定义」模式后再调节功耗与温控上限。";
        }
        if (_applyCustomButton != null) _applyCustomButton.IsEnabled = editable;

        _binding = true;
        try
        {
            foreach (var feature in control.Features)
            {
                if (!_featureSliders.TryGetValue(feature.Id, out var slot)) continue;
                int displayValue = Math.Clamp(feature.CurrentValue ?? feature.DefaultValue, feature.Min, feature.Max);
                slot.Slider.Value = displayValue;
                slot.Slider.IsEnabled = editable;
                slot.ValueText.Text = $"{displayValue} {feature.Unit}";
            }
        }
        finally { _binding = false; }
    }

    private void ApplySliderValues()
    {
        var control = _controller.Current.Control;
        if (control?.Ready != true) return;
        var values = new Dictionary<uint, int>();
        foreach (var pair in _featureSliders)
        {
            int raw = (int)Math.Round(pair.Value.Slider.Value);
            var f = pair.Value.Feature;
            int snapped = f.Min + ((Math.Clamp(raw, f.Min, f.Max) - f.Min) / f.Step) * f.Step;
            values[pair.Key] = snapped;
        }
        SetFeedback("正在按 BIOS 安全范围写入自定义参数…");
        _controller.ApplyFeatureValues(values, HardwareFeatureCatalog.ModeCustom, control.FanFullSpeed);
    }

    private void RenderPresetsSection(HardwareControlSnapshot? control)
    {
        if (control?.Ready != true)
        {
            _presetsCard.Visibility = Visibility.Collapsed;
            return;
        }
        _presetsCard.Visibility = Visibility.Visible;
        _presetListPanel.Children.Clear();

        var presets = _getPresets().Where(p =>
            string.Equals(p.VendorFamily, control.VendorFamily, StringComparison.OrdinalIgnoreCase)).ToList();
        if (presets.Count == 0)
        {
            var empty = Ui.Text("暂无已保存方案。调整好模式或参数后输入名称保存，启动时不自动重放。", 11);
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _presetListPanel.Children.Add(empty);
            return;
        }

        foreach (var preset in presets)
        {
            var captured = preset;
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var del = Ui.Button("删除", async () => await DeletePresetAsync(captured.Id));
            del.SetResourceReference(FrameworkElement.StyleProperty, "QuietButton");
            del.Padding = new Thickness(6, 3, 6, 3);
            del.Margin = new Thickness(4, 0, 0, 0);
            AutomationProperties.SetName(del, $"删除方案：{captured.Name}");
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);

            var apply = Ui.Button("应用", () => ApplyPreset(captured));
            apply.Padding = new Thickness(8, 3, 8, 3);
            apply.Margin = new Thickness(6, 0, 0, 0);
            AutomationProperties.SetName(apply, $"应用方案：{captured.Name}");
            DockPanel.SetDock(apply, Dock.Right);
            row.Children.Add(apply);

            string summary = $"{captured.Name} · {HardwareFeatureCatalog.ModeLabel(captured.ThermalMode)}模式" +
                (captured.FeatureValues.Count > 0 ? $"（{captured.FeatureValues.Count} 项参数）" : "");
            var label = Ui.Text(summary, 11.5);
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
            _presetListPanel.Children.Add(row);
        }
    }

    private async Task SaveCurrentPresetAsync()
    {
        var control = _controller.Current.Control;
        if (control?.Ready != true || control.ThermalMode == null)
        {
            SetFeedback("当前未连接提权服务，无法保存硬件方案。");
            return;
        }
        string name = _presetNameBox.Text.Trim();
        if (name.Length == 0)
        {
            SetFeedback("请先输入性能方案名称。");
            return;
        }

        var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (control.ThermalMode == HardwareFeatureCatalog.ModeCustom)
        {
            foreach (var pair in _featureSliders)
                values[pair.Value.Feature.HexId] = (int)Math.Round(pair.Value.Slider.Value);
        }

        var next = _getPresets().Select(p => p.Copy()).ToList();
        next.RemoveAll(p => string.Equals(p.VendorFamily, control.VendorFamily, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        next.Add(new HardwarePerformancePreset
        {
            Name = name,
            VendorFamily = control.VendorFamily,
            ThermalMode = control.ThermalMode.Value,
            FanFullSpeed = control.SupportsFanFullSpeed ? control.FanFullSpeed : null,
            FeatureValues = values
        });

        bool saved = await _savePresets(next);
        if (_disposed) return;
        if (saved)
        {
            _presetNameBox.Clear();
            SetFeedback($"方案「{name}」已保存。");
            RenderPresetsSection(_controller.Current.Control);
        }
        else SetFeedback("保存方案失败，请检查配置目录写入权限。");
    }

    private async Task DeletePresetAsync(string id)
    {
        var next = _getPresets().Where(p => p.Id != id).Select(p => p.Copy()).ToList();
        bool saved = await _savePresets(next);
        if (_disposed) return;
        SetFeedback(saved ? "方案已删除。" : "删除方案失败。");
        if (saved) RenderPresetsSection(_controller.Current.Control);
    }

    private void ApplyPreset(HardwarePerformancePreset preset)
    {
        var parsed = new Dictionary<uint, int>();
        foreach (var pair in preset.FeatureValues)
        {
            if (uint.TryParse(pair.Key, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id))
                parsed[id] = pair.Value;
        }
        SetFeedback($"正在应用方案「{preset.Name}」…");
        _controller.ApplyFeatureValues(parsed, preset.ThermalMode, preset.FanFullSpeed);
    }

    private static string BuildCompactSummary(HardwareSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Error))
            return snapshot.Error!;

        var parts = new List<string>(3);
        string fan = HardwareCapabilityEvaluator.FormatFanRpm(snapshot.Control);
        if (fan.Length > 0) parts.Add(fan);

        if (snapshot.GpuTelemetry is { } gpu)
        {
            if (gpu.PowerDrawWatts is { } draw)
            {
                string power = gpu.EnforcedLimitWatts is { } lim
                    ? $"GPU {draw.ToString("F1", CultureInfo.InvariantCulture)} / {lim.ToString("F0", CultureInfo.InvariantCulture)} W"
                    : $"GPU {draw.ToString("F1", CultureInfo.InvariantCulture)} W";
                parts.Add(power);
            }
            if (gpu.GraphicsClockMhz is { } clock && fan.Length == 0)
                parts.Add($"{clock} MHz");
        }
        else if (snapshot.Identity.PhysicalGpus.Count > 0)
        {
            parts.Add(snapshot.Identity.PhysicalGpus[0].Name);
        }

        int controllable = snapshot.Capabilities.Count(item => item.State == HardwareCapabilityState.AvailableControllable);
        int elevated = snapshot.Capabilities.Count(item => item.State == HardwareCapabilityState.RequiresElevatedService);
        int readOnly = snapshot.Capabilities.Count(item => item.State == HardwareCapabilityState.AvailableReadOnly);
        if (controllable > 0)
            parts.Add($"{controllable} 项可调节");
        else if (elevated > 0)
            parts.Add($"{elevated} 项待更新提权服务");
        else if (readOnly > 0)
            parts.Add($"{readOnly} 项只读监测");
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

    private void RenderCapabilities(IReadOnlyList<HardwareCapabilityItem> items)
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
            bool highlighted = item.State is HardwareCapabilityState.AvailableControllable or HardwareCapabilityState.RequiresElevatedService;
            badge.SetResourceReference(Border.BackgroundProperty, highlighted ? "Accent" : "SliderTrack");
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, highlighted ? "OnAccent" : "Ink");
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
