using System.Windows;
using System.Windows.Controls;

namespace FileDockDemo;

internal sealed partial class AppShortcutCatalogDialog
{
    private static string MappingSuffix(ShortcutBinding? binding) => binding == null ? "" :
        $" · 映射 {MappingLabel(binding)}" + (binding.Enabled ? "" : "（已暂停）");

    private static string MappingLabel(ShortcutBinding? binding)
    {
        if (binding == null) return "";
        string label = binding.Label;
        string? key = binding.Key switch {
            >= '0' and <= '9' => ((char)binding.Key).ToString(),
            8 => "Backspace", 33 => "PageUp", 34 => "PageDown", _ => null
        };
        return key == null ? label : label[..(label.LastIndexOf(' ') + 1)] + key;
    }

    private sealed class MappingEditor
    {
        private readonly ShortcutBinding? _original;
        private readonly TextBlock _summary = Ui.Text("");
        private readonly TextBlock _hint = Ui.Text("", 12);
        private readonly TextBox _keys = Field("新快捷键", 64);
        private readonly Button _add, _toggle, _remove;
        private bool _configured, _enabled;
        private bool _clearOnSave;
        private string? _unavailable;
        internal StackPanel View { get; } = new();
        internal bool HasChanges => _configured != (_original != null) || (_configured &&
            (_enabled != (_original?.Enabled ?? false) || _keys.Text != MappingLabel(_original)));

        internal MappingEditor(ShortcutBinding? original)
        {
            _original = original;
            _configured = original != null;
            _enabled = original?.Enabled ?? false;
            _keys.Text = MappingLabel(original);
            _add = CompactButton("添加映射", () => {
                _configured = _enabled = true;
                Render();
                _keys.Focus();
            });
            _toggle = CompactButton("", () => { _enabled = !_enabled; Render(); });
            _remove = CompactButton("删除映射", () => {
                _configured = _enabled = false;
                _keys.Clear();
                Render();
            });
            var controls = new WrapPanel();
            controls.Children.Add(_add);
            controls.Children.Add(_toggle);
            controls.Children.Add(_remove);
            View.Children.Add(_summary);
            View.Children.Add(_hint);
            View.Children.Add(_keys);
            View.Children.Add(controls);
        }

        internal void SetAvailability(string? reason, bool clearOnSave = false)
        {
            _unavailable = reason;
            _clearOnSave = reason != null && clearOnSave;
            Render();
        }

        private void Render()
        {
            bool available = _unavailable == null;
            bool retain = !available && !_clearOnSave && _configured && _original != null;
            _summary.Text = "前台映射 · " + (!_configured || (!available && !retain) ? "未设置" : _enabled ? "启用" : "已暂停");
            _hint.Text = !available ? _unavailable + (_clearOnSave ? "保存后将清除原映射。" :
                retain ? $" 已设置的键位为 {MappingLabel(_original)}。" : "") : (_configured
                ? "用文字输入新快捷键，如 Ctrl+Alt+J。保存后，仅在该程序位于前台时发送原快捷键。"
                : "仅在该程序位于前台时生效。");
            _keys.Visibility = available && _configured ? Visibility.Visible : Visibility.Collapsed;
            _toggle.Visibility = (available && _configured) || (retain && _enabled) ? Visibility.Visible : Visibility.Collapsed;
            _remove.Visibility = (available && _configured) || retain ? Visibility.Visible : Visibility.Collapsed;
            _add.Visibility = available && !_configured ? Visibility.Visible : Visibility.Collapsed;
            _toggle.Content = _enabled ? "暂停映射" : "启用映射";
        }

        internal bool TryRead(out ShortcutBinding? binding, out string error)
        {
            binding = null;
            error = "";
            if (!_configured || _clearOnSave) return true;
            if (_unavailable != null)
            {
                binding = _original?.Copy();
                if (binding != null) binding.Enabled = _enabled;
                return true;
            }
            if (_original != null && _keys.Text == MappingLabel(_original))
            {
                binding = _original.Copy();
                binding.Enabled = _enabled;
                return true;
            }
            if (!AppMappingRules.TryParse(_keys.Text, out var chord) || (chord.Modifiers & 3) == 0)
            {
                error = "请输入一组含 Ctrl 或 Alt 的新快捷键，并避开系统保留键，例如 Ctrl+Alt+J。";
                return false;
            }
            binding = new ShortcutBinding { Enabled = _enabled, Modifiers = chord.Modifiers, Key = chord.Key };
            return true;
        }
    }
}
