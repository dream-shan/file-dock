using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FileDockDemo.Features.Screenshot;

internal sealed class ScreenshotOverlay : Form
{
    private readonly Bitmap _frame;
    private readonly Rectangle _desktop;
    private readonly FlowLayoutPanel _actions;
    private Point _anchor;
    private Rectangle _selection;
    private bool _dragging;
    private bool _dialogOpen;
    private bool _watchingSession;
    private bool _ready;
    internal string Result { get; private set; } = "已取消截图";
    internal string? Failure { get; private set; }

    internal ScreenshotOverlay(Bitmap frame, Rectangle desktop)
    {
        _frame = frame;
        _desktop = desktop;
        Text = "文件坞截图";
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Bounds = desktop;
        Cursor = Cursors.Cross;
        KeyPreview = true;
        _actions = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            BackColor = Color.FromArgb(39, 43, 50), Padding = new Padding(4), Cursor = Cursors.Default,
            Visible = false, AccessibleName = "截图操作"
        };
        AddAction("复制", Copy);
        AddAction("另存为 PNG", Save);
        AddAction("取消", Close);
        Controls.Add(_actions);
    }

    private void AddAction(string title, Action action)
    {
        var button = new Button
        {
            Text = title, AccessibleName = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Padding = new Padding(6, 2, 6, 2), Margin = new Padding(2)
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => action();
        _actions.Controls.Add(button);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Form sizing may clamp a borderless window to a single monitor's maximum
        // tracking size. Place its client area in the same physical coordinates as BitBlt.
        if (!SetWindowPos(Handle, new IntPtr(-1), _desktop.X, _desktop.Y, _desktop.Width, _desktop.Height, 0)
            || RectangleToScreen(ClientRectangle) != _desktop)
        {
            Failure = "桌面尺寸已变化，请重新截图。";
            Close();
            return;
        }
        _ready = true;
        Activate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _watchingSession = WTSRegisterSessionNotification(Handle, 0);
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_watchingSession) WTSUnRegisterSessionNotification(Handle);
        _watchingSession = false;
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0024 && _desktop.Width > 0)
        {
            base.WndProc(ref message);
            var sizes = Marshal.PtrToStructure<MinMaxInfo>(message.LParam);
            sizes.MaxTrack.X = Math.Max(sizes.MaxTrack.X, _desktop.Width);
            sizes.MaxTrack.Y = Math.Max(sizes.MaxTrack.Y, _desktop.Height);
            Marshal.StructureToPtr(sizes, message.LParam, false);
            return;
        }
        // Discard stale screen pixels if geometry, power, or login session changes.
        if (Visible && (message.Msg == 0x007E || (_ready && message.Msg == 0x02E0)
            || (_ready && !_dialogOpen && message.Msg == 0x001C && message.WParam == 0)
            || (message.Msg == 0x0218 && message.WParam == 4)
            || (message.Msg == 0x02B1 && message.WParam.ToInt32() is 2 or 4 or 7)))
        {
            // A native save dialog has its own nested message loop. Close it
            // too, so the desktop bitmap is released when the session locks.
            IntPtr popup = GetLastActivePopup(Handle);
            if (_dialogOpen && popup != IntPtr.Zero && popup != Handle)
                PostMessage(popup, 0x0010, IntPtr.Zero, IntPtr.Zero);
            Hide();
            BeginInvoke(new Action(Close));
        }
        base.WndProc(ref message);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData != Keys.Escape) return base.ProcessCmdKey(ref message, keyData);
        Close();
        return true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right) { Close(); return; }
        if (e.Button != MouseButtons.Left) return;
        _actions.Hide();
        _selection = Rectangle.Empty;
        _anchor = PointToScreen(e.Location);
        _dragging = true;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        var previous = _selection;
        _selection = ScreenshotSession.Selection(_desktop, _anchor, PointToScreen(e.Location));
        var dirty = previous.IsEmpty ? _selection : Rectangle.Union(previous, _selection);
        dirty.Inflate(2, 2);
        Invalidate(dirty);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging || e.Button != MouseButtons.Left) return;
        _dragging = false;
        Capture = false;
        Point pointer = PointToScreen(e.Location);
        _selection = ScreenshotSession.Selection(_desktop, _anchor, pointer);
        if (!_selection.IsEmpty)
        {
            Rectangle monitor = Screen.FromPoint(pointer).Bounds;
            monitor.Offset(-_desktop.X, -_desktop.Y);
            _actions.Show();
            _actions.PerformLayout();
            int x = Math.Clamp(pointer.X - _desktop.X, monitor.Left, Math.Max(monitor.Left, monitor.Right - _actions.Width));
            int y = Math.Clamp(pointer.Y - _desktop.Y + 8, monitor.Top, Math.Max(monitor.Top, monitor.Bottom - _actions.Height));
            _actions.Location = new Point(x, y);
        }
        Invalidate();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!_dragging || Capture) return;
        _dragging = false;
        _selection = Rectangle.Empty;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        Rectangle clip = Rectangle.Intersect(e.ClipRectangle, new Rectangle(Point.Empty, _frame.Size));
        if (clip.IsEmpty) return;
        e.Graphics.DrawImage(_frame, clip, clip, GraphicsUnit.Pixel);
        using var outside = new Region(clip);
        if (!_selection.IsEmpty) outside.Exclude(_selection);
        using var shade = new SolidBrush(Color.FromArgb(85, Color.Black));
        e.Graphics.FillRegion(shade, outside);
        if (!_selection.IsEmpty)
        {
            using var pen = new Pen(Color.DeepSkyBlue, 1);
            e.Graphics.DrawRectangle(pen, _selection.X, _selection.Y, _selection.Width - 1, _selection.Height - 1);
        }
        else if (!_dragging)
        {
            var monitor = Screen.FromPoint(Cursor.Position).Bounds;
            var hint = new Point(monitor.Left - _desktop.X + 20, monitor.Top - _desktop.Y + 20);
            TextRenderer.DrawText(e.Graphics, "拖动选择区域 · 右键取消", Font, hint, Color.White, Color.FromArgb(38, 42, 48));
        }
    }

    private Bitmap Crop() => _frame.Clone(_selection, PixelFormat.Format24bppRgb);

    private void Copy()
    {
        WithDialog(() =>
        {
            using var image = Crop();
            using var png = ScreenshotSession.EncodePng(image);
            var data = new DataObject();
            data.SetImage(image);
            data.SetData("PNG", false, png);
            Clipboard.SetDataObject(data, true, 5, 50);
            Result = "截图已复制";
            Close();
        });
    }

    private void Save()
    {
        WithDialog(() =>
        {
            using var dialog = new SaveFileDialog
            {
                Title = "保存截图", Filter = "PNG 图片 (*.png)|*.png", DefaultExt = "png", AddExtension = true,
                OverwritePrompt = true, RestoreDirectory = true, FileName = $"截图-{DateTime.Now:yyyyMMdd-HHmmss}.png"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK || !Visible) return;
            SaveSelection(dialog.FileName);
        });
    }

    internal void SaveSelection(string target)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(target)!, $".FileDock-{Guid.NewGuid():N}.tmp");
        using var image = Crop();
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                image.Save(stream, ImageFormat.Png);
            }
            File.Move(temporary, target, true);
            created = false;
        }
        finally { if (created) File.Delete(temporary); }
        Result = "截图已保存";
        Close();
    }

    private void WithDialog(Action operation)
    {
        if (_dialogOpen || _selection.IsEmpty) return;
        _dialogOpen = true;
        try { operation(); }
        catch (Exception ex) when (ex is IOException or ExternalException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            if (Visible) MessageBox.Show(this, ex.Message, "截图操作失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { _dialogOpen = false; }
    }

    [DllImport("wtsapi32.dll")] private static extern bool WTSRegisterSessionNotification(IntPtr window, uint flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(IntPtr window);
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr order, int x, int y,
        int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
}
