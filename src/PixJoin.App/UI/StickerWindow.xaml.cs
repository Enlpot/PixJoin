using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PixJoin.App.Native;
using PixJoin.Core.Models;

namespace PixJoin.App.UI;

/// <summary>
/// 一张桌面贴图窗口：无边框、置顶、可调透明度。
/// 位置与尺寸一律通过 SetWindowPos 以「物理像素」设置，避免 WPF 的 DPI 换算误差；
/// 内容用 Stretch=Fill 铺满，因此窗口物理尺寸即贴图物理尺寸。
/// </summary>
public sealed partial class StickerWindow : Window
{
    private readonly StickerManager _owner;
    private readonly HwndSourceHook? _hook;
    private HwndSource? _hwndSource;
    private bool _topmost = true;

    /// <summary>拖拽 / 缩放开始前的几何快照（物理像素）。</summary>
    internal (double X, double Y, double W, double H) GeometrySnapshot { get; set; }

    /// <summary>缩放拖拽开始时的鼠标物理位置（屏幕坐标）；用总位移计算，避免 Thumb 相对坐标漂移。</summary>
    private Point _resizeOriginPhysical;

    private DateTime _lastLeftUpTime = DateTime.MinValue;   // 双击检测：上次左键抬起时间
    private Point _lastLeftUpPos;                            // 双击检测：上次左键抬起位置（窗口坐标）

    public Sticker Sticker { get; }

    public IntPtr Handle { get; private set; }

    public StickerWindow(Sticker sticker, StickerManager owner)
    {
        InitializeComponent();
        Sticker = sticker;
        _owner = owner;

        Img.Source = sticker.Image;
        Opacity = sticker.Opacity;

        SourceInitialized += OnSourceInitialized;
        DpiChanged += (_, _) => ApplyGeometry();

        MouseEnter += (_, _) => RefreshFrame();
        MouseLeave += (_, _) => RefreshFrame();

        PreviewMouseLeftButtonDown += OnBodyMouseDown;
        MouseMove += OnBodyMouseMove;
        PreviewMouseLeftButtonUp += OnBodyMouseUp;
        PreviewMouseRightButtonUp += OnRightButtonUp;
        MouseWheel += OnMouseWheel;

        HandleTL.DragDelta += (_, _) => OnResizeDragDelta(left: true, top: true);
        HandleTR.DragDelta += (_, _) => OnResizeDragDelta(left: false, top: true);
        HandleBL.DragDelta += (_, _) => OnResizeDragDelta(left: true, top: false);
        HandleBR.DragDelta += (_, _) => OnResizeDragDelta(left: false, top: false);
        HandleTL.DragStarted += OnResizeDragStarted;
        HandleTR.DragStarted += OnResizeDragStarted;
        HandleBL.DragStarted += OnResizeDragStarted;
        HandleBR.DragStarted += OnResizeDragStarted;
        HandleTL.DragCompleted += (_, _) => _owner.OnStickerResized(this);
        HandleTR.DragCompleted += (_, _) => _owner.OnStickerResized(this);
        HandleBL.DragCompleted += (_, _) => _owner.OnStickerResized(this);
        HandleBR.DragCompleted += (_, _) => _owner.OnStickerResized(this);

        _hook = WndProc;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        Handle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(Handle);
        _hwndSource?.AddHook(_hook);

        // 不在 Alt+Tab 中出现
        Win32.AddExStyle(Handle, Win32.WS_EX_TOOLWINDOW);
        ApplyGeometry();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 点击贴图时不抢走前台应用的焦点
        if (msg == Win32.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(Win32.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    /// <summary>把模型里的物理像素几何应用到窗口。</summary>
    public void ApplyGeometry()
    {
        if (Handle == IntPtr.Zero) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        double s = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;

        int x = (int)Math.Round(Sticker.X);
        int y = (int)Math.Round(Sticker.Y);
        int w = Math.Max(8, (int)Math.Round(Sticker.W));
        int h = Math.Max(8, (int)Math.Round(Sticker.H));

        // 关键：同时把 WPF 逻辑尺寸（DIP）设为「物理尺寸 / DPI 缩放比」，
        // 让 WPF 的布局尺寸与下面 SetWindowPos 的物理尺寸严格一致。
        // 否则 WPF 会以内容（Image 原始像素）自动定尺寸，把窗口拽回内容大小，
        // 在 Per-Monitor V2 下表现为贴图偏大/偏小、缩放手柄看似失效或变形（Bug 1 / Bug 2）。
        Width = w / s;
        Height = h / s;

        Win32.SetWindowPos(Handle, _topmost ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST,
            x, y, w, h, Win32.SWP_NOACTIVATE);
    }

    public void SetTopmost(bool topmost)
    {
        _topmost = topmost;
        ApplyGeometry();
    }

    /// <summary>刷新边框与外框可见性：始终显示——组合青色实线、单张淡白描边（悬停更亮），缩放手柄常驻。</summary>
    public void RefreshFrame()
    {
        if (Sticker.IsGrouped)
            Frame.BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0xE5, 0xC0));
        else if (IsMouseOver)
            Frame.BorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF));
        else
            Frame.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));

        HandleLayer.Visibility = Visibility.Visible;
    }

    public void SetOpacity(double value)
    {
        Sticker.Opacity = Math.Clamp(value, 0.15, 1.0);
        Opacity = Sticker.Opacity;
    }

    // ---------------- 交互 ----------------

    private void OnBodyMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.Source is Thumb) return;   // 手柄自己处理

        CaptureMouse();
        _owner.BeginDrag(this, e.GetPosition(this));
        e.Handled = true;
    }

    private void OnBodyMouseMove(object sender, MouseEventArgs e)
    {
        if (!_owner.IsDragging) return;
        _owner.UpdateDrag(this);
    }

    private void OnBodyMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_owner.IsDragging) return;
        if (IsMouseCaptured) ReleaseMouseCapture();
        _owner.EndDrag(this);
        e.Handled = true;

        // 手动双击检测：OnBodyMouseDown 在 Preview 阶段设 Handled 会抑制 WPF 自带 MouseDoubleClick，
        // 改在 MouseUp 处用「时间 + 位置」判双击（仅贴图主体，缩放手柄不算）。
        if (e.ChangedButton != MouseButton.Left || e.Source is Thumb) return;
        var now = DateTime.Now;
        var pos = e.GetPosition(this);
        bool isDouble = (now - _lastLeftUpTime).TotalMilliseconds < 500
                        && Math.Abs(pos.X - _lastLeftUpPos.X) < 6
                        && Math.Abs(pos.Y - _lastLeftUpPos.Y) < 6;
        _lastLeftUpTime = now;
        _lastLeftUpPos = pos;
        if (isDouble)
        {
            if (_owner.ConfirmDoubleClickClose(this)) _owner.CloseSticker(this);
        }
    }

    private void OnRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var menu = BuildContextMenu();
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        if (alt)
        {
            // Alt + 滚轮：调整透明度
            SetOpacity(Sticker.Opacity + (e.Delta > 0 ? 0.1 : -0.1));
            e.Handled = true;
        }
        else
        {
            // 直接滚轮（新增，首次提示）或 Ctrl+滚轮（原有）：以中心为锚点缩放贴图
            if (!ctrl && !_owner.ConfirmWheelZoom(this)) { e.Handled = true; return; }

            // 每格 3%（等比）：锚点由吸附状态决定——单边吸附贴边缩放，否则中心缩放
            double f = e.Delta > 0 ? 1.03 : 1.0 / 1.03;
            _owner.ApplyWheelZoom(this, f);
            e.Handled = true;
        }
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.Source is Thumb) return;   // 缩放手柄上的双击不算
        if (!_owner.ConfirmDoubleClickClose(this)) return;
        _owner.CloseSticker(this);
        e.Handled = true;
    }

    private void OnResizeDragStarted(object? sender, DragStartedEventArgs e)
    {
        GeometrySnapshot = (Sticker.X, Sticker.Y, Sticker.W, Sticker.H);
        _resizeOriginPhysical = GetCursorPhysical();
    }

    private void OnResizeDragDelta(bool left, bool top)
    {
        // 用「相对拖拽起点的物理光标总位移」，而不是 WPF Thumb 的每帧相对坐标增量：
        // Thumb 的增量会随窗口位置变化（左/上角拖拽时窗口会移动）而漂移，导致缩放乱跳。
        var cur = GetCursorPhysical();
        double dx = cur.X - _resizeOriginPhysical.X;
        double dy = cur.Y - _resizeOriginPhysical.Y;
        ResizeBy(dx, dy, left, top);
    }

    private void ResizeBy(double dx, double dy, bool left, bool top)
    {
        var (ox, oy, ow, oh) = GeometrySnapshot;

        // 默认等比缩放（保持纵横比）；按住 Shift 切换为自由拉伸。
        bool keepAspect = (Keyboard.Modifiers & ModifierKeys.Shift) == 0;

        var (nw, nh) = StickerGeometry.ComputeResize(ow, oh, dx, dy, left, top, keepAspect);

        nw = Math.Max(16, nw);
        nh = Math.Max(16, nh);

        // 缩放边缘对齐吸附：移动边到达其它贴图边缘时自动磁吸（固定对角不动）
        (nw, nh) = _owner.ApplyResizeAlign(this, nw, nh, left, top);

        Sticker.W = nw;
        Sticker.H = nh;
        if (left) Sticker.X = ox + ow - nw;
        if (top) Sticker.Y = oy + oh - nh;

        ApplyGeometry();
    }

    private static Point GetCursorPhysical()
    {
        Win32.GetPhysicalCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        bool grouped = Sticker.IsGrouped;

        var copy = new MenuItem { Header = grouped ? "复制组合体" : "复制到剪贴板" };
        copy.Click += (_, _) => _owner.CopyStickerOrGroup(this);
        menu.Items.Add(copy);

        var save = new MenuItem { Header = grouped ? "组合体另存为…" : "另存为…" };
        save.Click += (_, _) => _owner.SaveStickerOrGroup(this);
        menu.Items.Add(save);

        menu.Items.Add(new Separator());

        var detach = new MenuItem { Header = "拆离此贴图", IsEnabled = grouped };
        detach.Click += (_, _) => _owner.DetachSticker(this);
        menu.Items.Add(detach);

        var dissolve = new MenuItem { Header = "全部解散", IsEnabled = grouped };
        dissolve.Click += (_, _) => _owner.DissolveGroup(this);
        menu.Items.Add(dissolve);

        menu.Items.Add(new Separator());

        var opacityRoot = new MenuItem { Header = "透明度" };
        foreach (var v in new[] { 0.25, 0.5, 0.75, 1.0 })
        {
            var value = v;
            var item = new MenuItem
            {
                Header = $"{(int)(v * 100)}%",
                IsChecked = Math.Abs(Sticker.Opacity - v) < 0.01,
            };
            item.Click += (_, _) => SetOpacity(value);
            opacityRoot.Items.Add(item);
        }
        menu.Items.Add(opacityRoot);

        var topmost = new MenuItem { Header = "置顶", IsChecked = _topmost };
        topmost.Click += (_, _) => { SetTopmost(!_topmost); };
        menu.Items.Add(topmost);

        menu.Items.Add(new Separator());

        var close = new MenuItem { Header = "关闭" };
        close.Click += (_, _) => _owner.CloseSticker(this);
        menu.Items.Add(close);

        var closeAll = new MenuItem { Header = "关闭全部贴图" };
        closeAll.Click += (_, _) => _owner.CloseAll();
        menu.Items.Add(closeAll);

        return menu;
    }

    protected override void OnClosed(EventArgs e)
    {
        _hwndSource?.RemoveHook(_hook);
        _owner.OnWindowClosed(this);
        base.OnClosed(e);
    }
}
