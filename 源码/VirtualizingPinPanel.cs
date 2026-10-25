using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FileDockDemo;

// Two columns with pixel scrolling. Keep only visible rows and one buffer row.
public sealed class VirtualizingPinPanel : VirtualizingPanel, IScrollInfo
{
    private const double RowHeight = 76;
    private Size _extent, _viewport;
    private double _offset;
    public VirtualizingPinPanel() { ClipToBounds = true; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = ItemsControl.GetItemsOwner(this);
        int count = items?.Items.Count ?? 0;
        double width = double.IsInfinity(availableSize.Width) ? 360 : availableSize.Width;
        double height = Math.Ceiling(count / 2d) * RowHeight;
        _extent = new Size(width, height);
        _viewport = new Size(width, double.IsInfinity(availableSize.Height) ? Math.Min(height, 158) : availableSize.Height);
        _offset = Math.Clamp(_offset, 0, Math.Max(0, height - _viewport.Height));
        ScrollOwner?.InvalidateScrollInfo();
        int first = Math.Max(0, (int)(_offset / RowHeight) - 1) * 2;
        int last = Math.Min(count - 1, ((int)Math.Ceiling((_offset + _viewport.Height) / RowHeight) + 1) * 2 - 1);
        // First access connects WPF's generator. Existing saved pins can reach
        // the first measure before ItemContainerGenerator has been initialized.
        _ = InternalChildren;
        var generator = ItemContainerGenerator;

        // Remove before generating so the live visual count stays bounded even
        // when the scrollbar jumps from the first row to the last row.
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            int index = generator.IndexFromGeneratorPosition(position);
            if (index < first || index > last)
            {
                generator.Remove(position, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
        if (last >= first)
        {
            var position = generator.GeneratorPositionFromIndex(first);
            int childIndex = position.Offset == 0 ? position.Index : position.Index + 1;
            using (generator.StartAt(position, GeneratorDirection.Forward, true))
            {
                for (int index = first; index <= last; index++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out bool created);
                    if (created)
                    {
                        if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(new Size(width / 2, RowHeight));
                }
            }
        }
        return new Size(width, Math.Min(height, _viewport.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            InternalChildren[i].Arrange(new Rect((index % 2) * finalSize.Width / 2,
                (index / 2) * RowHeight - _offset, finalSize.Width / 2, RowHeight));
        }
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        // The bound snapshot is replaced on filtering/reordering. Rebuild the
        // small visible range, never the entire collection.
        ItemContainerGenerator.RemoveAll();
        RemoveInternalChildRange(0, InternalChildren.Count);
        _offset = 0;
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        double top = (index / 2) * RowHeight;
        if (top < _offset) SetVerticalOffset(top);
        else if (top + RowHeight > _offset + _viewport.Height) SetVerticalOffset(top + RowHeight - _viewport.Height);
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        DependencyObject? child = visual;
        while (child != null && VisualTreeHelper.GetParent(child) != this)
            child = VisualTreeHelper.GetParent(child);
        if (child == null) return Rect.Empty;
        var owner = ItemsControl.GetItemsOwner(this);
        int index = owner.ItemContainerGenerator.IndexFromContainer(child);
        if (index >= 0) BringIndexIntoView(index);
        return rectangle;
    }
    public void SetVerticalOffset(double offset)
    {
        if (double.IsNaN(offset)) return;
        double next = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        if (Math.Abs(next - _offset) < 0.01) return;
        _offset = next; InvalidateMeasure(); ScrollOwner?.InvalidateScrollInfo();
    }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset;
    public ScrollViewer? ScrollOwner { get; set; }
    public void LineUp() => SetVerticalOffset(_offset - RowHeight);
    public void LineDown() => SetVerticalOffset(_offset + RowHeight);
    public void MouseWheelUp() => SetVerticalOffset(_offset - RowHeight * 2);
    public void MouseWheelDown() => SetVerticalOffset(_offset + RowHeight * 2);
    public void PageUp() => SetVerticalOffset(_offset - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset + _viewport.Height);
    public void SetHorizontalOffset(double offset) { }
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageLeft() { }
    public void PageRight() { }
}
