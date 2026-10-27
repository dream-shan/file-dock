using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;

namespace FileDockDemo;

public sealed class PerformanceCard : Border
{
    private readonly TextBlock _label, _value, _detail, _temperature;
    private readonly PerformanceGraph _graph;
    private readonly Grid _grid;
    private readonly PerformanceRing _ring;
    private bool _ringMode;
    public string Label { get => _label.Text; set { _label.Text = value; _graph.MetricLabel = value; } }
    public Brush Accent { get => _graph.Accent; set { _graph.Accent = value; _value.Foreground = value; _ring.Accent = value; _ring.InvalidateVisual(); } }
    public bool Ring
    {
        get => _ringMode;
        set
        {
            if (_ringMode == value) return;
            _ringMode = value;
            _grid.RowDefinitions.Clear(); _grid.ColumnDefinitions.Clear();
            _grid.VerticalAlignment = value ? VerticalAlignment.Center : VerticalAlignment.Stretch;
            _ring.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            _graph.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            _temperature.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            foreach (var text in new[] { _label, _value, _detail }) Grid.SetColumn(text, value ? 1 : 0);
            _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _grid.RowDefinitions.Add(new RowDefinition { Height = value ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
            _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (value)
            {
                _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetRow(_label,0); Grid.SetRow(_value,1); Grid.SetRow(_detail,2);
                Grid.SetRowSpan(_ring,3); UpdateRingSize();
            }
            else { _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) }); Trend = Trend; }
        }
    }
    private void UpdateRingSize()
    {
        if (!Ring) return;
        bool compact = _grid.ActualWidth > 0 && _grid.ActualWidth < 145;
        _ring.Width = _ring.Height = compact ? 40 : 50;
        _ring.Margin = new Thickness(0,0,compact ? 8 : 10,0);
    }
    public bool Trend
    {
        get => _graph.Trend;
        set
        {
            _graph.Trend = value;
            if (Ring) return;
            _grid.RowDefinitions[1].Height = value ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            _grid.RowDefinitions[2].Height = value ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            _grid.RowDefinitions[3].Height = value ? GridLength.Auto : new GridLength(18);
            Grid.SetRow(_graph, value ? 2 : 3); Grid.SetRow(_detail, value ? 3 : 2);
            _graph.MaxHeight = value ? 60 : double.PositiveInfinity;
            _graph.VerticalAlignment = VerticalAlignment.Stretch;
            _detail.Text = value ? "最近 60 秒" : "正在读取…";
            _detail.FontSize = value ? 8 : 9;
            _detail.Margin = value ? new Thickness(0, 2, 0, 0) : new Thickness(0);
        }
    }
    public string ValueText => _value.Text;
    public string TemperatureText => _temperature.Text;
    internal int HistoryCount => _graph.HistoryCount;
    internal PerformanceGraph Graph => _graph;
    internal string DetailText => _detail.Text;
    internal PerformanceRing RingChart => _ring;
    internal bool CapacityFits => _detail.ActualWidth >= new FormattedText(_detail.Text,
        System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(_detail.FontFamily,_detail.FontStyle,_detail.FontWeight,_detail.FontStretch),
        _detail.FontSize,_detail.Foreground,VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
    internal void ApplyTheme(Brush accent)
    {
        SetResourceReference(BackgroundProperty, "Card");
        _label.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        _temperature.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Accent = accent; _graph.Track = ThemePalette.Brush("Track"); _graph.InvalidateVisual();
        _ring.Track = ThemePalette.Brush("Track"); _ring.InvalidateVisual();
    }

    public PerformanceCard()
    {
        CornerRadius = new CornerRadius(12); Background = new SolidColorBrush(Color.FromRgb(243, 246, 252)); Padding = new Thickness(12, 7, 12, 5);
        var grid = _grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
        _label = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(74, 87, 110)) };
        _temperature = new TextBlock { FontSize = 10.5, Foreground = Brushes.SlateGray, HorizontalAlignment = HorizontalAlignment.Right };
        _value = new TextBlock { Text = "—", FontSize = 25, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _detail = new TextBlock { Text = "正在读取…", FontSize = 9, Foreground = Brushes.SlateGray, TextTrimming = TextTrimming.CharacterEllipsis };
        _graph = new PerformanceGraph();
        _ring = new PerformanceRing { Width=50,Height=50,VerticalAlignment=VerticalAlignment.Center,Visibility=Visibility.Collapsed };
        Grid.SetRow(_value, 1); Grid.SetRow(_detail, 2); Grid.SetRow(_graph, 3);
        grid.Children.Add(_label); grid.Children.Add(_temperature); grid.Children.Add(_value); grid.Children.Add(_detail); grid.Children.Add(_graph);
        grid.Children.Add(_ring); grid.SizeChanged += (_,_) => UpdateRingSize();
        Child = grid; Accent = new SolidColorBrush(Color.FromRgb(74, 108, 247));
        System.Windows.Automation.AutomationProperties.SetName(this, "性能指标");
    }
    internal void Set(double? percent, string detail, double? temperature = null, bool showTemperature = false)
    {
        if (Ring) percent = PerformanceRing.Normalize(percent);
        _value.Text = percent.HasValue ? $"{Math.Clamp(percent.Value, 0, 100):0}%" : "—";
        _detail.Text = Trend ? "最近 60 秒" : detail;
        _temperature.Text = showTemperature ? temperature.HasValue ? $"{temperature:0}°C" : "温度 —" : "";
        if (Ring) _ring.Set(percent); else _graph.Add(percent);
        System.Windows.Automation.AutomationProperties.SetName(this, $"{Label} {_value.Text} {_temperature.Text} {_detail.Text}");
    }
    internal void Reset()
    {
        _graph.Clear(); _value.Text = "—"; _detail.Text = Trend ? "最近 60 秒" : "正在读取…";
        _ring.Set(null);
        if (_temperature.Text.Length > 0) _temperature.Text = "温度 —";
    }
}

internal sealed class PerformanceRing : FrameworkElement
{
    internal Brush Accent = Brushes.Teal;
    internal Brush Track = Brushes.LightGray;
    internal double? Percent { get; private set; }
    internal static double? Normalize(double? value) => value.HasValue && double.IsFinite(value.Value) ? Math.Clamp(value.Value,0,100) : null;
    internal void Set(double? value) { Percent=Normalize(value); InvalidateVisual(); }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        double size=Math.Min(ActualWidth,ActualHeight);
        if(size < 8) return;
        double thickness=size*.12, radius=(size-thickness)/2;
        var center=new Point(ActualWidth/2,ActualHeight/2);
        drawing.DrawEllipse(null,new Pen(Track,thickness),center,radius,radius);
        if(Percent is not > 0) return;
        var pen=new Pen(Accent,thickness) { StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round };
        if(Percent >= 100) { drawing.DrawEllipse(null,pen,center,radius,radius); return; }
        double angle=Percent.Value*Math.PI/50;
        var geometry=new StreamGeometry();
        using(var context=geometry.Open())
        {
            context.BeginFigure(new Point(center.X,center.Y-radius),false,false);
            context.ArcTo(new Point(center.X+radius*Math.Sin(angle),center.Y-radius*Math.Cos(angle)),
                new Size(radius,radius),0,Percent>50,SweepDirection.Clockwise,true,false);
        }
        geometry.Freeze(); drawing.DrawGeometry(null,pen,geometry);
    }
}

internal sealed class PerformanceGraph : FrameworkElement
{
    internal const int MaximumSamples = 60;
    private readonly Queue<double?> _values = new();
    private readonly ToolTip _hoverTip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse, Content = "正在读取…" };
    private int _hoverIndex = -1;
    private Brush? _areaBrush, _areaAccent;
    internal string MetricLabel = "";
    internal Brush Accent = Brushes.RoyalBlue;
    internal Brush Track = new SolidColorBrush(Color.FromRgb(224, 231, 242));
    private bool _trend;
    internal bool Trend { get => _trend; set { _trend = value; ToolTip = value ? _hoverTip : null; } }
    internal int HistoryCount => _values.Count;
    internal PerformanceGraph()
    {
        ToolTipService.SetInitialShowDelay(this, 150);
        ToolTipService.SetShowDuration(this, 60000);
        MouseLeave += (_, _) => _hoverIndex = -1;
    }
    internal void Add(double? value)
    {
        _values.Enqueue(value.HasValue && double.IsFinite(value.Value) ? Math.Clamp(value.Value, 0, 100) : null);
        while (_values.Count > MaximumSamples) _values.Dequeue();
        _hoverIndex = -1;
        if (IsMouseOver) UpdateHover(Mouse.GetPosition(this).X);
        InvalidateVisual();
    }
    internal void Clear() { _values.Clear(); _hoverIndex = -1; _hoverTip.IsOpen = false; InvalidateVisual(); }
    internal static double PlotY(double percent, double height) => 3 + Math.Max(0, height - 6) * (1 - Math.Clamp(percent, 0, 100) / 100);
    internal string HoverText(double x, double width)
    {
        int slot = Math.Clamp((int)Math.Round(x / Math.Max(1, width) * (MaximumSamples - 1)), 0, MaximumSamples - 1);
        int index = slot - (MaximumSamples - _values.Count);
        if (index < 0 || index >= _values.Count) return "此时段暂无采样";
        double? value = _values.ElementAt(index);
        string age = slot == MaximumSamples - 1 ? "最新采样" : $"{MaximumSamples - 1 - slot} 秒前";
        return $"{MetricLabel} · {age} · " + (value.HasValue ? $"{value.Value:0.#}%" : "暂不可用");
    }
    private void UpdateHover(double x)
    {
        int slot = Math.Clamp((int)Math.Round(x / Math.Max(1, ActualWidth) * (MaximumSamples - 1)), 0, MaximumSamples - 1);
        if (_hoverIndex == slot) return;
        _hoverIndex = slot; _hoverTip.Content = HoverText(x, ActualWidth);
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (Trend) UpdateHover(e.GetPosition(this).X); }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        double w = ActualWidth, h = ActualHeight - 6;
        if (w <= 0 || h <= 0) return;
        var background = Track;
        if (!Trend)
        {
            drawing.DrawRoundedRectangle(background, null, new Rect(0, h / 2, w, 4), 2, 2);
            double? last = null; foreach (var value in _values) last = value;
            if (last.HasValue) drawing.DrawRoundedRectangle(Accent, null, new Rect(0, h / 2, w * Math.Clamp(last.Value, 0, 100) / 100, 4), 2, 2);
            return;
        }
        // The transparent surface also makes the entire chart available for hover.
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
        var gridPen = new Pen(background, .6) { DashStyle = DashStyles.Dot };
        foreach (double percent in new[] { 25d, 50d, 75d })
            drawing.DrawLine(gridPen, new Point(0, PlotY(percent, ActualHeight)), new Point(w, PlotY(percent, ActualHeight)));
        drawing.DrawLine(new Pen(background, .8), new Point(0, h + 3), new Point(w, h + 3));
        if (!ReferenceEquals(_areaAccent, Accent)) { _areaAccent = Accent; _areaBrush = Accent.Clone(); _areaBrush.Opacity *= .12; _areaBrush.Freeze(); }
        var pen = new Pen(Accent, 2) { LineJoin = PenLineJoin.Round };
        var segment = new List<Point>();
        void DrawSegment()
        {
            if (segment.Count == 0) return;
            var area = new StreamGeometry();
            using (var context = area.Open())
            {
                context.BeginFigure(new Point(segment[0].X, h + 3), true, true);
                foreach (var point in segment) context.LineTo(point, true, false);
                context.LineTo(new Point(segment[^1].X, h + 3), true, false);
            }
            area.Freeze(); drawing.DrawGeometry(_areaBrush, null, area);
            if (segment.Count == 1) drawing.DrawEllipse(Accent, null, segment[0], 1.7, 1.7);
            else
            {
                var line = new StreamGeometry();
                using (var context = line.Open()) { context.BeginFigure(segment[0], false, false); foreach (var point in segment.Skip(1)) context.LineTo(point, true, false); }
                line.Freeze(); drawing.DrawGeometry(null, pen, line);
            }
            segment.Clear();
        }
        int index = MaximumSamples - _values.Count;
        foreach (var value in _values)
        {
            if (value.HasValue)
            {
                segment.Add(new Point(index * w / (MaximumSamples - 1), PlotY(value.Value, ActualHeight)));
            }
            else DrawSegment();
            index++;
        }
        DrawSegment();
    }
}

