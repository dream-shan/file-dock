using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FileDockDemo;

// A narrow opposing outline keeps the glyph legible over both bright and dark desktop content.
public sealed class HeaderGlyph : Control
{
    static HeaderGlyph()
    {
        ForegroundProperty.OverrideMetadata(typeof(HeaderGlyph),
            new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    }
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(HeaderGlyph),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HaloColorProperty = DependencyProperty.Register(
        nameof(HaloColor), typeof(Color), typeof(HeaderGlyph),
        new FrameworkPropertyMetadata(Colors.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Color HaloColor { get => (Color)GetValue(HaloColorProperty); set => SetValue(HaloColorProperty, value); }

    protected override void OnRender(DrawingContext drawing)
    {
        var text = new FormattedText(Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var geometry = text.BuildGeometry(new Point((ActualWidth - text.Width) / 2, (ActualHeight - text.Height) / 2));
        var outline = new Pen(new SolidColorBrush(HaloColor), 1.5) { LineJoin = PenLineJoin.Round };
        drawing.DrawGeometry(null, outline, geometry);
        drawing.DrawGeometry(Foreground, null, geometry);
    }
}
