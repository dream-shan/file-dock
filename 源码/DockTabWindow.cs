using System;
using System.Drawing;
using System.Windows.Forms;

namespace FileDockDemo;

// A native utility form keeps the small translucent entry independent of the
// WPF per-pixel window surface used by the main panel.
internal sealed class DockTabWindow : Form
{
    private readonly MainWindow _owner;
    private Point _start;
    private bool _moved;
    private bool _dark;
    private double? _memoryPercent;
    private Color _entryAccent, _entryBorder, _entryBackground;
    private Timer? _pressureTimer;
    private long _pressureStart;
    private Color _fromAccent,_fromBorder,_fromBackground,_toAccent,_toBorder,_toBackground;
    internal Color MemoryAccentColor=>_entryAccent;
    internal void SetMemoryPressure(double? percent,bool animate=true)
    {
        _memoryPercent=percent;
        Color normalAccent=_dark ? Color.FromArgb(130,179,255) : Color.FromArgb(41,111,235);
        Color normalBorder=_dark ? Color.FromArgb(149,162,181) : Color.FromArgb(180,197,220);
        Color normalBackground=_dark ? Color.FromArgb(48,53,62) : Color.White;
        double blend=percent.HasValue ? Math.Clamp((percent.Value-65)/15,0,1) : 0;
        var pressure=MemoryPressure.ColorFor(percent??0,_dark);
        Color pressureColor=Color.FromArgb(pressure.R,pressure.G,pressure.B);
        Color Mix(Color a,Color b,double t) => Color.FromArgb((int)Math.Round(a.R+(b.R-a.R)*t),(int)Math.Round(a.G+(b.G-a.G)*t),(int)Math.Round(a.B+(b.B-a.B)*t));
        _toAccent=Mix(normalAccent,pressureColor,blend); _toBorder=Mix(normalBorder,pressureColor,blend);
        _toBackground=blend<=0 ? normalBackground : Mix(normalBackground,pressureColor,.14*blend);
        _pressureTimer?.Stop();
        if(!animate || !Visible || _entryAccent.IsEmpty) { _entryAccent=_toAccent;_entryBorder=_toBorder;_entryBackground=_toBackground;BackColor=_entryBackground;Invalidate();return; }
        if(_entryAccent==_toAccent && _entryBorder==_toBorder && _entryBackground==_toBackground)return;
        _fromAccent=_entryAccent;_fromBorder=_entryBorder;_fromBackground=_entryBackground;_pressureStart=Environment.TickCount64;
        if(_pressureTimer==null){_pressureTimer=new Timer {Interval=30};_pressureTimer.Tick+=(_,_)=>{
            double t=Math.Clamp((Environment.TickCount64-_pressureStart)/300d,0,1);
            _entryAccent=Mix(_fromAccent,_toAccent,t);_entryBorder=Mix(_fromBorder,_toBorder,t);_entryBackground=Mix(_fromBackground,_toBackground,t);
            BackColor=_entryBackground;Invalidate();if(t>=1)_pressureTimer.Stop();
        };}
        _pressureTimer.Start();
    }
    internal void ApplyTheme(bool dark)
    {
        _dark = dark;
        SetMemoryPressure(_memoryPercent,false);
        if (ContextMenuStrip != null) ThemeMenus.Apply(ContextMenuStrip, dark);
        Invalidate();
    }
    internal bool IsVisible => Visible;
    internal double ScaleFactor => NativeMethods.WindowDpi(Handle) / 96d;
    internal new double Width => 20;
    internal new double Height => 48;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal new double Left { get => base.Left / ScaleFactor; set => base.Left = (int)Math.Round(value * ScaleFactor); }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal new double Top { get => base.Top / ScaleFactor; set => base.Top = (int)Math.Round(value * ScaleFactor); }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle = (p.ExStyle | 0x80) & ~0x40000; return p; } }

    internal DockTabWindow(MainWindow owner)
    {
        _owner = owner; Text = "文件坞入口";
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.Manual;
        BackColor = Color.White; Opacity = 0.38; Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        var menu = new ContextMenuStrip();
        menu.Items.Add("展开文件坞", null, (_, _) => _owner.ExpandPanel());
        menu.Items.Add("退出", null, (_, _) => _owner.Quit()); ContextMenuStrip = menu;
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.HideFromTaskSwitcher(Handle);
        // Remote desktop is also screen capture: permanent exclusion would
        // leave an invisible but clickable entry in the remote user's view.
        NativeMethods.SetWindowDisplayAffinity(Handle, 0);
    }
    private void UpdateShape()
    {
        ClientSize = new Size((int)Math.Round(Width * ScaleFactor), (int)Math.Round(Height * ScaleFactor));
        NativeMethods.RoundEdgeWindow(Handle, Width, Height);
    }
    internal new void Show()
    {
        SetMemoryPressure(_memoryPercent,false);
        UpdateShape(); Opacity = 0.38; base.Show();
        NativeMethods.SetWindowDisplayAffinity(Handle, 0);
        Invalidate();
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); UpdateShape(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); float scale = (float)ScaleFactor;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var border = new Pen(_entryBorder, scale);
        e.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        using var pen = new Pen(_entryAccent, 1.5f * scale);
        float center = ClientSize.Width / 2f, y = ClientSize.Height / 2f;
        float direction = _owner.State.DockSide == "Left" ? -1 : 1;
        e.Graphics.DrawLines(pen, new[] { new PointF(center + direction * 2 * scale, y - 4 * scale), new PointF(center - direction * 2 * scale, y), new PointF(center + direction * 2 * scale, y + 4 * scale) });
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Opacity = 0.9; }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (!Capture) Opacity = 0.38; }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return;
        _start = e.Location; _moved = false; Capture = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); if (!Capture || e.Button != MouseButtons.Left) return;
        int dx = e.X - _start.X, dy = e.Y - _start.Y;
        if (Math.Abs(dx) + Math.Abs(dy) > 4 * ScaleFactor) _moved = true;
        if (_moved) { base.Left += dx; base.Top += dy; }
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e); if (e.Button != MouseButtons.Left || !Capture) return;
        Capture = false;
        if (_moved) _owner.DockMoved(Left, Top); else _owner.ExpandPanel();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _pressureTimer?.Dispose(); ContextMenuStrip?.Dispose(); }
        base.Dispose(disposing);
    }
}
