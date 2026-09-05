using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using PixJoin.App.Native;
using PixJoin.Core.Models;
using PixJoin.Core.Services;

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

    // ---- 文字选择（OCR） ----
    private bool _selectingText;                 // 正在拖选文字
    private int _selectStart = -1;               // 起点词索引（文档顺序）
    private int _selectEnd = -1;                 // 终点词索引
    private bool _textSelectable = true;         // 贴图级「文本可选择」开关（右键菜单）
    private readonly SolidColorBrush _selectBrush = new(Color.FromArgb(0x55, 0x33, 0x88, 0xFF));
    private FloatingBarWindow? _floating;        // 文字选择浮动工具条（独立小窗口，可超出贴图窗口）

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
        if (Sticker.IsLocked)
        {
            Frame.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xCC, 0xCC, 0xCC));
            HandleLayer.Visibility = Visibility.Collapsed;
            return;
        }

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

        // 锁定：禁止一切主体交互（右键菜单仍可用）
        if (Sticker.IsLocked) { e.Handled = true; return; }

        // Alt 按住 → 强制移动贴图（跳过文字选择，PixPin 同款）
        bool altForced = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        if (!altForced && CanTextSelect())
        {
            var img = ToImageCoord(e.GetPosition(this));
            int idx = OcrSelection.HitTest(Sticker.OcrWords!, img.X, img.Y);
            if (idx >= 0)
            {
                BeginTextSelection(idx);
                e.Handled = true;
                return;
            }
        }

        // 空白处按下：清掉已有文字选区（若有），然后走拖动
        ClearTextSelection();

        CaptureMouse();
        _owner.BeginDrag(this, e.GetPosition(this));
        e.Handled = true;
    }

    private void OnBodyMouseMove(object sender, MouseEventArgs e)
    {
        if (_owner.IsDragging) { _owner.UpdateDrag(this); return; }

        if (_selectingText)
        {
            UpdateTextSelection(e.GetPosition(this));
            return;
        }

        UpdateTextCursor(e.GetPosition(this));
    }

    private void OnBodyMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_selectingText)
        {
            EndTextSelection(e.GetPosition(this));
            e.Handled = true;
            return;
        }

        if (!_owner.IsDragging) return;
        if (IsMouseCaptured) ReleaseMouseCapture();
        _owner.EndDrag(this);
        e.Handled = true;

        // 手动双击检测：OnBodyMouseDown 在 Preview 阶段设 Handled 会抑制 WPF 自带 MouseDoubleClick，
        // 改在 MouseUp 处用「时间 + 位置」判双击（仅贴图主体，缩放手柄不算）。
        if (e.ChangedButton != MouseButton.Left || e.Source is Thumb) return;
        if (Sticker.IsLocked) return;
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
        // 锁定：滚轮缩放 / 调透明度都禁用
        if (Sticker.IsLocked) { e.Handled = true; return; }

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

            // 缩放会改变图片显示尺寸，选区高亮坐标随之失效 → 清除选区
            ClearTextSelection();

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
        // 缩放会改变图片显示尺寸，选区高亮坐标随之失效 → 清除选区
        ClearTextSelection();
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

        // ---- OCR / 文字识别 ----
        bool hasOcr = Sticker.OcrWords is { Count: > 0 };
        var copyAllText = new MenuItem { Header = "复制所有文本（Shift+C）", IsEnabled = hasOcr };
        copyAllText.Click += (_, _) => _owner.CopyAllOcrText(this);
        menu.Items.Add(copyAllText);

        var textSelect = new MenuItem { Header = "文本可选择", IsChecked = _textSelectable };
        textSelect.Click += (_, _) =>
        {
            _textSelectable = !_textSelectable;
            if (!_textSelectable) ClearTextSelection();
        };
        menu.Items.Add(textSelect);

        menu.Items.Add(new Separator());

        var hideOthers = new MenuItem { Header = _owner.HasHiddenStickers ? "显示全部贴图" : "隐藏其他贴图" };
        hideOthers.Click += (_, _) => _owner.ToggleHideOthers(this);
        menu.Items.Add(hideOthers);

        var lockItem = new MenuItem { Header = "锁定", IsChecked = Sticker.IsLocked };
        lockItem.Click += (_, _) =>
        {
            Sticker.IsLocked = !Sticker.IsLocked;
            if (Sticker.IsLocked) ClearTextSelection();
            RefreshFrame();
        };
        menu.Items.Add(lockItem);

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

    // ---------------- 文字选择（OCR） ----------------

    /// <summary>是否可进行文字选择：贴图级开关 + 全局 OCR 开关 + 引擎可用 + 已有识别结果。</summary>
    private bool CanTextSelect() =>
        _textSelectable && !Sticker.IsLocked && _owner.OcrEnabled && Sticker.OcrWords is { Count: > 0 };

    /// <summary>OCR 完成后由 Manager 回调：有词则激活选择层（悬停命中时显示 IBeam）。</summary>
    public void OnOcrReady()
    {
        if (Sticker.OcrWords is { Count: > 0 })
            OverlayLayer.Visibility = Visibility.Visible;
    }

    /// <summary>窗口 DIP 坐标 → 图片物理像素坐标（Image Stretch=Fill，按实际显示尺寸换算）。</summary>
    private Point ToImageCoord(Point dip)
    {
        int pw = Sticker.Image.PixelWidth;
        int ph = Sticker.Image.PixelHeight;
        double iw = Img.ActualWidth > 0 ? Img.ActualWidth : pw;
        double ih = Img.ActualHeight > 0 ? Img.ActualHeight : ph;
        return new Point(dip.X * pw / iw, dip.Y * ph / ih);
    }

    /// <summary>悬停时光标切换：词内 IBeam，否则箭头。</summary>
    private void UpdateTextCursor(Point dip)
    {
        if (!CanTextSelect())
        {
            if (Cursor != Cursors.Arrow) Cursor = Cursors.Arrow;
            return;
        }
        var img = ToImageCoord(dip);
        int idx = OcrSelection.HitTest(Sticker.OcrWords!, img.X, img.Y);
        Cursor = idx >= 0 ? Cursors.IBeam : Cursors.Arrow;
    }

    private void BeginTextSelection(int idx)
    {
        _selectingText = true;
        _selectStart = idx;
        _selectEnd = idx;
        CaptureMouse();
        HideFloatingBar();
        RefreshSelectionLayer();
    }

    private void UpdateTextSelection(Point dip)
    {
        var img = ToImageCoord(dip);
        int idx = OcrSelection.HitTest(Sticker.OcrWords!, img.X, img.Y);
        if (idx < 0 || idx == _selectEnd) return;
        _selectEnd = idx;
        RefreshSelectionLayer();
    }

    private void EndTextSelection(Point endPos)
    {
        _selectingText = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        RefreshSelectionLayer();
        if (_selectStart >= 0 && _selectEnd >= 0) ShowFloatingBar(endPos);
        else ClearTextSelection();
    }

    /// <summary>把 [start, end] 内所有词画为半透明高亮（DIP 坐标）。</summary>
    private void RefreshSelectionLayer()
    {
        SelectLayer.Children.Clear();
        if (_selectStart < 0 || _selectEnd < 0) return;
        var words = Sticker.OcrWords;
        if (words is null || words.Count == 0) return;

        var (s, e) = OcrSelection.Normalize(_selectStart, _selectEnd);
        double sx = Img.ActualWidth > 0 ? Img.ActualWidth / Sticker.Image.PixelWidth : 1;
        double sy = Img.ActualHeight > 0 ? Img.ActualHeight / Sticker.Image.PixelHeight : 1;

        for (int i = s; i <= e; i++)
        {
            var w = words[i];
            var rect = new Rectangle { Fill = _selectBrush, Width = w.W * sx, Height = w.H * sy };
            Canvas.SetLeft(rect, w.X * sx);
            Canvas.SetTop(rect, w.Y * sy);
            SelectLayer.Children.Add(rect);
        }
    }

    /// <summary>浮动工具条在鼠标松手点附近弹出（独立小窗口，可超出贴图窗口；贴屏幕边缘自动翻转）。</summary>
    private void ShowFloatingBar(Point endPos)
    {
        var words = Sticker.OcrWords;
        if (words is null || words.Count == 0) return;

        if (_floating is null)
        {
            _floating = new FloatingBarWindow();
            _floating.Owner = this;
            _floating.CopyRequested += OnCopySelection;
            _floating.DismissRequested += OnDismissSelection;
        }

        // 贴图窗口内坐标 → 屏幕坐标（WPF DIP，多显示器负坐标亦正确）
        Point screen = PointToScreen(endPos);

        // 先测量获得真实尺寸（未显示时 ActualWidth 为 0）
        _floating.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double bw = _floating.DesiredSize.Width;
        double bh = _floating.DesiredSize.Height;

        const double gap = 8;
        double x = screen.X + gap;
        double y = screen.Y + gap;
        // 超出虚拟屏幕右/下边缘 → 翻转到鼠标左/上方
        double vsRight = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        double vsBottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        if (x + bw > vsRight) x = screen.X - bw - gap;
        if (y + bh > vsBottom) y = screen.Y - bh - gap;

        _floating.Left = x;
        _floating.Top = y;
        if (!_floating.IsVisible) _floating.Show();
    }

    private void HideFloatingBar() => _floating?.Hide();

    private void ClearTextSelection()
    {
        _selectingText = false;
        _selectStart = -1;
        _selectEnd = -1;
        SelectLayer.Children.Clear();
        HideFloatingBar();
    }

    /// <summary>ESC 优先级：有活动文字选区 / 浮动条时先取消选择（返回 true 拦截 ESC），不关闭贴图。</summary>
    public bool CancelTextSelectionIfActive()
    {
        if (!_selectingText && _selectStart < 0 && (_floating is null || !_floating.IsVisible)) return false;
        ClearTextSelection();
        return true;
    }

    private void OnCopySelection()
    {
        if (_selectStart < 0 || _selectEnd < 0) return;
        _owner.CopyOcrSelection(this, _selectStart, _selectEnd);
        ClearTextSelection();
    }

    private void OnDismissSelection() => ClearTextSelection();
    /// <summary>鼠标穿透（托盘全局开关）：点击事件穿过贴图到下层窗口。</summary>
    public void SetClickThrough(bool on)
    {
        if (Handle == IntPtr.Zero) return;
        int ex = Win32.GetWindowLong(Handle, Win32.GWL_EXSTYLE);
        int next = on ? ex | Win32.WS_EX_TRANSPARENT : ex & ~Win32.WS_EX_TRANSPARENT;
        if (next != ex) Win32.SetWindowLong(Handle, Win32.GWL_EXSTYLE, next);
    }

    protected override void OnClosed(EventArgs e)
    {
        _hwndSource?.RemoveHook(_hook);
        _floating?.Close();   // 独立浮动条随贴图关闭（Owner 机制也会关，双保险）
        _owner.OnWindowClosed(this);
        base.OnClosed(e);
    }
}
