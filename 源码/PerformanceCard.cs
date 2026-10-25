using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FileDockDemo;

public sealed class PerformanceCard : Border
{
    private readonly TextBlock _label, _value, _detail, _temperature;
    private readonly PerformanceGraph _graph;
    public string Label { get => _label.Text; set => _label.Text = value; }
    public Brush Accent { get => _graph.Accent; set { _graph.Accent = value; _value.Foreground = value; } }
    public bool Trend { get => _graph.Trend; set => _graph.Trend = value; }
    public string ValueText => _value.Text;
    public string TemperatureText => _temperature.Text;
    internal int HistoryCount => _graph.HistoryCount;

    public PerformanceCard()
    {
        CornerRadius = new CornerRadius(12); Background = new SolidColorBrush(Color.FromRgb(243, 246, 252)); Padding = new Thickness(12, 7, 12, 5);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
        _label = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(74, 87, 110)) };
        _temperature = new TextBlock { FontSize = 10.5, Foreground = Brushes.SlateGray, HorizontalAlignment = HorizontalAlignment.Right };
        _value = new TextBlock { Text = "—", FontSize = 25, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _detail = new TextBlock { Text = "正在读取…", FontSize = 9, Foreground = Brushes.SlateGray, TextTrimming = TextTrimming.CharacterEllipsis };
        _graph = new PerformanceGraph();
        Grid.SetRow(_value, 1); Grid.SetRow(_detail, 2); Grid.SetRow(_graph, 3);
        grid.Children.Add(_label); grid.Children.Add(_temperature); grid.Children.Add(_value); grid.Children.Add(_detail); grid.Children.Add(_graph);
        Child = grid; Accent = new SolidColorBrush(Color.FromRgb(74, 108, 247));
        System.Windows.Automation.AutomationProperties.SetName(this, "性能指标");
    }
    internal void Set(double? percent, string detail, double? temperature = null, bool showTemperature = false)
    {
        _value.Text = percent.HasValue ? $"{Math.Clamp(percent.Value, 0, 100):0}%" : "—";
        _detail.Text = detail;
        _temperature.Text = showTemperature ? temperature.HasValue ? $"{temperature:0}°C" : "温度 —" : "";
        _graph.Add(percent);
        System.Windows.Automation.AutomationProperties.SetName(this, $"{Label} {_value.Text} {_temperature.Text} {detail}");
    }
    internal void Reset()
    {
        _graph.Clear(); _value.Text = "—"; _detail.Text = "正在读取…";
        if (_temperature.Text.Length > 0) _temperature.Text = "温度 —";
    }
}

internal sealed class PerformanceGraph : FrameworkElement
{
    private readonly Queue<double?> _values = new();
    internal Brush Accent = Brushes.RoyalBlue;
    internal bool Trend;
    internal int HistoryCount => _values.Count;
    internal void Add(double? value) { _values.Enqueue(value); while (_values.Count > 30) _values.Dequeue(); InvalidateVisual(); }
    internal void Clear() { _values.Clear(); InvalidateVisual(); }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        double w = ActualWidth, h = ActualHeight - 6;
        if (w <= 0 || h <= 0) return;
        var background = new SolidColorBrush(Color.FromRgb(224, 231, 242));
        if (!Trend)
        {
            drawing.DrawRoundedRectangle(background, null, new Rect(0, h / 2, w, 4), 2, 2);
            double? last = null; foreach (var value in _values) last = value;
            if (last.HasValue) drawing.DrawRoundedRectangle(Accent, null, new Rect(0, h / 2, w * Math.Clamp(last.Value, 0, 100) / 100, 4), 2, 2);
            return;
        }
        drawing.DrawLine(new Pen(background, 1), new Point(0, h + 3), new Point(w, h + 3));
        var pen = new Pen(Accent, 1.5);
        Point? previous = null; int index = 30 - _values.Count;
        foreach (var value in _values)
        {
            if (value.HasValue)
            {
                var point = new Point(index * w / 29, 3 + h * (1 - Math.Clamp(value.Value, 0, 100) / 100));
                if (previous.HasValue) drawing.DrawLine(pen, previous.Value, point);
                previous = point;
            }
            else previous = null;
            index++;
        }
    }
}

