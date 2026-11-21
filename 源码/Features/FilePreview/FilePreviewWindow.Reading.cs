using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Markdig.Syntax;

namespace FileDockDemo;

internal sealed partial class FilePreviewWindow
{
    private Button _previousFile = null!, _nextFile = null!;
    private readonly TextBlock _filePosition = Ui.Text("");
    private Action<int>? _moveFile;

    private void InitializeFileNavigation()
    {
        _previousFile = Ui.Button("上一个文件", () => _moveFile?.Invoke(-1));
        _nextFile = Ui.Button("下一个文件", () => _moveFile?.Invoke(1));
        _previousFile.Margin = _nextFile.Margin = new Thickness(0,0,6,6);
        _filePosition.VerticalAlignment = VerticalAlignment.Center;
        _fileNavigation.Children.Add(_previousFile);
        _fileNavigation.Children.Add(_nextFile);
        _fileNavigation.Children.Add(_filePosition);
    }

    internal void SetFileNavigation(int index, int count, Action<int> move)
    {
        _moveFile = move;
        _fileNavigation.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _previousFile.IsEnabled = index > 0;
        _nextFile.IsEnabled = index + 1 < count;
        _filePosition.Text = $"文件 {index + 1} / {count}";
    }

    private void AddPageControls(uint page)
    {
        AddControl("上一页", () => ChangePage(page - 1), page > 0);
        AddControl("下一页", () => ChangePage(page + 1), page + 1 < _pages);
        var entry = InputBox("目标页码", 50);
        entry.Text = (page + 1).ToString();
        var jump = new StackPanel { Orientation = Orientation.Horizontal };
        var error = Ui.Text("");
        jump.Children.Add(entry);
        var go = Ui.Button("跳转", () => {
            if (uint.TryParse(entry.Text, out uint target) && target >= 1 && target <= _pages)
            {
                error.Text = "";
                ChangePage(target - 1);
            }
            else
            {
                error.Text = $"请输入 1 到 {_pages} 的页码";
                entry.Focus();
                entry.SelectAll();
            }
        });
        go.Margin = new Thickness(0,4,4,0);
        jump.Children.Add(go);
        _controls.Children.Add(jump);
        _controls.Children.Add(error);
    }

    private void ShowText(string text, bool wrap)
    {
        var (view, find) = TextView(text, wrap);
        _content.Content = view;
        AddControl("查找", find);
    }

    private void ShowMarkdown(string text, MarkdownDocument document)
    {
        var rendered = MarkdownPreview.Create(document);
        var (raw, find) = TextView(text, true);
        _content.Content = rendered;
        Button? viewToggle = null, search = null;
        void SetReading(bool reading)
        {
            _content.Content = reading ? rendered : raw;
            viewToggle!.Content = reading ? "查看原文" : "阅读视图";
            search!.Content = reading ? "查找原文" : "查找";
        }
        viewToggle = AddControl("查看原文", () => SetReading(ReferenceEquals(_content.Content, raw)));
        search = AddControl("查找原文", () => { SetReading(false); find(); });
    }

    private static (FrameworkElement View, Action Find) TextView(string text, bool wrap)
    {
        var layout = new DockPanel();
        var bar = new WrapPanel { Visibility = Visibility.Collapsed };
        DockPanel.SetDock(bar, Dock.Top);
        layout.Children.Add(bar);
        var box = new TextBox {
            Text = text, IsReadOnly = true, IsUndoEnabled = false, BorderThickness = new Thickness(0),
            Padding = new Thickness(8), FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsInactiveSelectionHighlightEnabled = true
        };
        box.SetResourceReference(Control.BackgroundProperty, "ContentSurface");
        box.SetResourceReference(Control.ForegroundProperty, "Ink");
        layout.Children.Add(box);

        var query = InputBox("查找文字", 130);
        var result = Ui.Text("");
        var positions = new List<int>();
        int selected = 0;
        var previous = Ui.Button("上一处", () => ShowMatch(-1));
        var next = Ui.Button("下一处", () => ShowMatch(1));
        var close = Ui.Button("关闭查找", () => {
            query.Clear();
            bar.Visibility = Visibility.Collapsed;
            box.Focus();
        });
        foreach (var button in new[] { previous, next, close }) button.Margin = new Thickness(0,4,4,4);
        foreach (var control in new FrameworkElement[] { query, previous, next, close, result }) bar.Children.Add(control);
        previous.IsEnabled = next.IsEnabled = false;
        query.TextChanged += (_, _) => {
            positions.Clear();
            string value = query.Text;
            if (value.Length > 0)
            {
                for (int start = 0, match; (match = text.IndexOf(value, start, StringComparison.OrdinalIgnoreCase)) >= 0;)
                {
                    positions.Add(match);
                    start = match + value.Length;
                }
            }
            selected = 0;
            previous.IsEnabled = next.IsEnabled = positions.Count > 1;
            ShowMatch(0);
        };

        void ShowMatch(int step)
        {
            if (positions.Count == 0)
            {
                box.Select(box.SelectionStart, 0);
                result.Text = query.Text.Length == 0 ? "" : "未找到";
                return;
            }
            selected = (selected + step + positions.Count) % positions.Count;
            int position = positions[selected];
            box.Select(position, query.Text.Length);
            var rect = box.GetRectFromCharacterIndex(position);
            if (!rect.IsEmpty)
            {
                if (rect.Top < 0 || rect.Bottom >= box.ViewportHeight)
                    box.ScrollToVerticalOffset(Math.Max(0, box.VerticalOffset + rect.Top - box.Padding.Top));
                if (!wrap && (rect.Left < 0 || rect.Left >= box.ViewportWidth))
                    box.ScrollToHorizontalOffset(Math.Max(0, box.HorizontalOffset + rect.Left - box.Padding.Left));
            }
            result.Text = $"{selected + 1} / {positions.Count}";
        }

        return (layout, () => {
            bar.Visibility = Visibility.Visible;
            query.Focus();
            query.SelectAll();
        });
    }

    private static TextBox InputBox(string accessibleName, double width)
    {
        var box = new TextBox {
            Width = width, MinHeight = 30, Margin = new Thickness(0,4,4,4),
            VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(5,0,5,0)
        };
        box.SetResourceReference(Control.BackgroundProperty, "ContentSurface");
        box.SetResourceReference(Control.ForegroundProperty, "Ink");
        box.SetResourceReference(Control.BorderBrushProperty, "Border");
        AutomationProperties.SetName(box, accessibleName);
        return box;
    }
}
