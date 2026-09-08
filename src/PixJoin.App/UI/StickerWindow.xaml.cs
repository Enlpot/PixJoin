using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    // ---- 标注 ----
    private AnnotationBarWindow? _annotBar;
    private readonly List<Annotation> _annotations = new();
    private AnnotationTool _annotTool = AnnotationTool.Arrow;
    private Color _annotColor = Color.FromRgb(0xE5, 0x39, 0x35);
    private double _annotThickness = 4;
    private bool _annotating;                    // 标注模式
    private bool _annotDrawing;                  // 正在拖动绘制
    private Point _annotStart;                   // 拖动起点（窗口 DIP）
    private Point _annotLast;                    // 当前点（窗口 DIP）
    private List<double> _annotPenPts = new();
    private int _annotNumberSeq;

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

        // 标注模式：全部交给标注绘制
        if (_annotating)
        {
            HandleAnnotationDown(e.GetPosition(this));
            e.Handled = true;
            return;
        }

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
        if (_annotating)
        {
            HandleAnnotationMove(e.GetPosition(this));
            return;
        }

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
        if (_annotating)
        {
            HandleAnnotationUp(e.GetPosition(this));
            e.Handled = true;
            return;
        }

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
        // 标注模式：右键不弹菜单（避免误操作），Esc / ✓ 完成退出
        if (_annotating) { e.Handled = true; return; }

        var menu = BuildContextMenu();
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 标注模式：滚轮不缩放
        if (_annotating) { e.Handled = true; return; }

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

        // ---- 标注 / 图像处理 ----
        var annotate = new MenuItem { Header = "标注…", IsEnabled = !Sticker.IsLocked };
        annotate.Click += (_, _) => EnterAnnotationMode();
        menu.Items.Add(annotate);

        var imgOps = new MenuItem { Header = "图像处理", IsEnabled = !Sticker.IsLocked };
        AddImageOp(imgOps, "灰度", ImageProcessor.ToGrayscale);
        AddImageOp(imgOps, "反色", ImageProcessor.Invert);
        AddImageOp(imgOps, "模糊", ImageProcessor.Blur);
        AddImageOp(imgOps, "锐化", ImageProcessor.Sharpen);
        imgOps.Items.Add(new Separator());
        AddImageOp(imgOps, "旋转 90°", b => ImageProcessor.Rotate(b, 90));
        AddImageOp(imgOps, "旋转 180°", b => ImageProcessor.Rotate(b, 180));
        AddImageOp(imgOps, "旋转 270°", b => ImageProcessor.Rotate(b, 270));
        imgOps.Items.Add(new Separator());
        AddImageOp(imgOps, "水平翻转", ImageProcessor.FlipHorizontal);
        AddImageOp(imgOps, "垂直翻转", ImageProcessor.FlipVertical);
        menu.Items.Add(imgOps);

        var undoOp = new MenuItem { Header = "撤销标注 / 处理", IsEnabled = Sticker.UndoImage is not null };
        undoOp.Click += (_, _) => _owner.UndoImageOp(this);
        menu.Items.Add(undoOp);

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

    /// <summary>给图像处理子菜单添加一项。</summary>
    private void AddImageOp(MenuItem root, string header, Func<BitmapSource, BitmapSource> op)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => _owner.ApplyImageOp(this, op);
        root.Items.Add(item);
    }
    /// <summary>鼠标穿透（托盘全局开关）：点击事件穿过贴图到下层窗口。</summary>
    public void SetClickThrough(bool on)
    {
        if (Handle == IntPtr.Zero) return;
        int ex = Win32.GetWindowLong(Handle, Win32.GWL_EXSTYLE);
        int next = on ? ex | Win32.WS_EX_TRANSPARENT : ex & ~Win32.WS_EX_TRANSPARENT;
        if (next != ex) Win32.SetWindowLong(Handle, Win32.GWL_EXSTYLE, next);
    }

    // ---------------- 标注模式 ----------------

    /// <summary>进入标注模式：显示工具条与预览层，暂停拖动 / 缩放 / 文字选择。</summary>
    public void EnterAnnotationMode()
    {
        if (_annotating || Sticker.IsLocked) return;
        _annotating = true;
        _annotNumberSeq = 0;
        ClearTextSelection();

        if (_annotBar is null)
        {
            _annotBar = new AnnotationBarWindow();
            _annotBar.Owner = this;
            _annotBar.ToolChanged += t => _annotTool = t;
            _annotBar.ColorChanged += c => _annotColor = c;
            _annotBar.ThicknessChanged += t => _annotThickness = t;
            _annotBar.UndoRequested += UndoAnnotationStep;
            _annotBar.DoneRequested += () => ExitAnnotationMode(commit: true);
        }
        PositionAnnotationBar();
        _annotBar.Show();

        OverlayLayer.Visibility = Visibility.Visible;
        AnnotationLayer.Visibility = Visibility.Visible;
        HandleLayer.Visibility = Visibility.Collapsed;
        Cursor = Cursors.Cross;
    }

    /// <summary>退出标注模式。commit=true 时把标注固化进图片（可用右键「撤销标注」回退）。</summary>
    public void ExitAnnotationMode(bool commit)
    {
        if (!_annotating) return;
        _annotating = false;
        _annotDrawing = false;
        if (IsMouseCaptured) ReleaseMouseCapture();

        AnnotationLayer.Children.Clear();
        AnnotationLayer.Visibility = Visibility.Collapsed;
        _annotBar?.Hide();
        Cursor = Cursors.Arrow;

        if (commit && _annotations.Count > 0)
        {
            _owner.CommitAnnotations(this, _annotations);
        }
        _annotations.Clear();
        _annotPenPts.Clear();

        // 恢复叠加层可见性（有 OCR 词才显示）与缩放手柄
        OverlayLayer.Visibility = Sticker.OcrWords is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        RefreshFrame();
    }

    /// <summary>ESC / Ctrl+Z 钩子入口：标注模式下 Esc=固化退出、Ctrl+Z=撤销一步。返回是否拦截。</summary>
    public bool TryExitAnnotationMode()
    {
        if (!_annotating) return false;
        ExitAnnotationMode(commit: true);
        return true;
    }

    /// <summary>撤销上一步标注（标注模式内 Ctrl+Z / 工具条撤销按钮）。</summary>
    public void UndoAnnotationStep()
    {
        if (!_annotating || _annotations.Count == 0) return;
        _annotations.RemoveAt(_annotations.Count - 1);
        RefreshAnnotationLayer();
    }

    private void PositionAnnotationBar()
    {
        if (_annotBar is null) return;
        _annotBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double bw = _annotBar.DesiredSize.Width;
        double x = Left + ActualWidth + 8;
        if (x + bw > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth)
            x = Left - bw - 8;
        _annotBar.Left = Math.Max(SystemParameters.VirtualScreenLeft, x);
        _annotBar.Top = Math.Max(SystemParameters.VirtualScreenTop, Top);
    }

    private void HandleAnnotationDown(Point p)
    {
        if (_annotTool == AnnotationTool.Text || _annotTool == AnnotationTool.Number)
        {
            // 落点工具：立即生成元素，不进入拖动
            var img = ToImageCoord(p);
            if (_annotTool == AnnotationTool.Text)
            {
                var dlg = new TextInputWindow { Owner = this };
                if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.ResultText))
                {
                    _annotations.Add(new Annotation
                    {
                        Tool = AnnotationTool.Text, X = img.X, Y = img.Y,
                        Text = dlg.ResultText, Color = _annotColor, Thickness = _annotThickness,
                    });
                    RefreshAnnotationLayer();
                }
            }
            else
            {
                _annotNumberSeq++;
                _annotations.Add(new Annotation
                {
                    Tool = AnnotationTool.Number, X = img.X, Y = img.Y,
                    Number = _annotNumberSeq, Color = _annotColor, Thickness = _annotThickness,
                });
                RefreshAnnotationLayer();
            }
            return;
        }

        _annotDrawing = true;
        _annotStart = p;
        _annotLast = p;
        _annotPenPts.Clear();
        var ip = ToImageCoord(p);
        _annotPenPts.Add(ip.X);
        _annotPenPts.Add(ip.Y);
        CaptureMouse();
        RefreshAnnotationLayer();
    }

    private void HandleAnnotationMove(Point p)
    {
        if (!_annotDrawing) return;
        _annotLast = p;
        if (_annotTool == AnnotationTool.Pen)
        {
            var ip = ToImageCoord(p);
            _annotPenPts.Add(ip.X);
            _annotPenPts.Add(ip.Y);
        }
        RefreshAnnotationLayer();
    }

    private void HandleAnnotationUp(Point p)
    {
        if (!_annotDrawing) return;
        _annotDrawing = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        _annotLast = p;

        var s = ToImageCoord(_annotStart);
        var e = ToImageCoord(_annotLast);

        switch (_annotTool)
        {
            case AnnotationTool.Rect:
            case AnnotationTool.Highlight:
            case AnnotationTool.Mosaic:
                _annotations.Add(new Annotation
                {
                    Tool = _annotTool,
                    X = Math.Min(s.X, e.X), Y = Math.Min(s.Y, e.Y),
                    W = Math.Abs(e.X - s.X), H = Math.Abs(e.Y - s.Y),
                    Color = _annotColor, Thickness = _annotThickness,
                });
                break;

            case AnnotationTool.Arrow:
                _annotations.Add(new Annotation
                {
                    Tool = AnnotationTool.Arrow, X = s.X, Y = s.Y, W = e.X - s.X, H = e.Y - s.Y,
                    Color = _annotColor, Thickness = _annotThickness,
                });
                break;

            case AnnotationTool.Pen:
                if (_annotPenPts.Count >= 4)
                {
                    _annotations.Add(new Annotation
                    {
                        Tool = AnnotationTool.Pen, Points = _annotPenPts.ToArray(),
                        Color = _annotColor, Thickness = _annotThickness,
                    });
                }
                _annotPenPts.Clear();
                break;
        }

        RefreshAnnotationLayer();
    }

    /// <summary>把全部标注 + 进行中元素画到预览层（DIP 坐标，物理像素按显示缩放比换算）。</summary>
    private void RefreshAnnotationLayer()
    {
        AnnotationLayer.Children.Clear();
        foreach (var a in _annotations) AddAnnotationPreview(a);
        if (_annotDrawing)
        {
            // 进行中：用起点/终点构造临时预览
            var s = ToImageCoord(_annotStart);
            var e = ToImageCoord(_annotLast);
            var tool = _annotTool == AnnotationTool.Pen
                ? new Annotation { Tool = AnnotationTool.Pen, Points = _annotPenPts.ToArray(), Color = _annotColor, Thickness = _annotThickness }
                : new Annotation { Tool = _annotTool, X = Math.Min(s.X, e.X), Y = Math.Min(s.Y, e.Y), W = Math.Abs(e.X - s.X), H = Math.Abs(e.Y - s.Y), Color = _annotColor, Thickness = _annotThickness };
            AddAnnotationPreview(tool);
        }
    }

    /// <summary>物理像素 → 窗口 DIP（用于预览层定位）。</summary>
    private Point ToDip(Point phys) => new(
        phys.X * Img.ActualWidth / Sticker.Image.PixelWidth,
        phys.Y * Img.ActualHeight / Sticker.Image.PixelHeight);

    private void AddAnnotationPreview(Annotation a)
    {
        double sx = Img.ActualWidth / Sticker.Image.PixelWidth;
        double sy = Img.ActualHeight / Sticker.Image.PixelHeight;

        var brush = new SolidColorBrush(a.Color);
        brush.Freeze();
        var pen = new Pen(brush, Math.Max(1, a.Thickness * (sx + sy) / 2));
        pen.Freeze();

        switch (a.Tool)
        {
            case AnnotationTool.Rect:
            case AnnotationTool.Highlight:
            case AnnotationTool.Mosaic:
            {
                var fill = a.Tool == AnnotationTool.Highlight ? new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xE2, 0x3C))
                        : a.Tool == AnnotationTool.Mosaic ? new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00))
                        : null;
                fill?.Freeze();
                // WPF 闭合控件外描边：外扩 t/2 使描边中心回到几何边界，与最终渲染一致
                double th2 = a.Tool == AnnotationTool.Rect ? a.Thickness / 2 : 0;
                var r = new Rectangle { Width = Math.Max(1, (a.W + th2 * 2) * sx), Height = Math.Max(1, (a.H + th2 * 2) * sy), Stroke = a.Tool == AnnotationTool.Rect ? brush : null, Fill = fill };
                Canvas.SetLeft(r, (a.X - th2) * sx);
                Canvas.SetTop(r, (a.Y - th2) * sy);
                AnnotationLayer.Children.Add(r);
                break;
            }

            case AnnotationTool.Arrow:
            {
                var line = new Line
                {
                    X1 = a.X * sx, Y1 = a.Y * sy, X2 = (a.X + a.W) * sx, Y2 = (a.Y + a.H) * sy,
                    Stroke = brush, StrokeThickness = Math.Max(1, a.Thickness * (sx + sy) / 2),
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                };
                AnnotationLayer.Children.Add(line);
                break;
            }

            case AnnotationTool.Pen:
            {
                if (a.Points is { Length: >= 4 })
                {
                    var pts = new PointCollection();
                    for (int i = 0; i < a.Points.Length; i += 2)
                        pts.Add(new Point(a.Points[i] * sx, a.Points[i + 1] * sy));
                    var pl = new Polyline { Points = pts, Stroke = brush, StrokeThickness = Math.Max(1, a.Thickness * (sx + sy) / 2), StrokeLineJoin = PenLineJoin.Round };
                    AnnotationLayer.Children.Add(pl);
                }
                break;
            }

            case AnnotationTool.Text:
            {
                var tb = new TextBlock { Text = a.Text, Foreground = brush, FontSize = Math.Max(10, a.Thickness * 4 * (sx + sy) / 2) };
                Canvas.SetLeft(tb, a.X * sx);
                Canvas.SetTop(tb, a.Y * sy);
                AnnotationLayer.Children.Add(tb);
                break;
            }

            case AnnotationTool.Number:
            {
                double r = Math.Max(8, a.Thickness * 2.5 * (sx + sy) / 2);
                var ell = new Ellipse { Width = r * 2, Height = r * 2, Fill = brush };
                Canvas.SetLeft(ell, a.X * sx - r);
                Canvas.SetTop(ell, a.Y * sy - r);
                AnnotationLayer.Children.Add(ell);
                var tb = new TextBlock { Text = a.Number.ToString(), Foreground = Brushes.White, FontSize = r * 1.3, TextAlignment = TextAlignment.Center };
                Canvas.SetLeft(tb, a.X * sx - r * 0.55);
                Canvas.SetTop(tb, a.Y * sy - r * 0.8);
                AnnotationLayer.Children.Add(tb);
                break;
            }
        }
    }

    /// <summary>图片被标注 / 图像处理替换后刷新显示（更新源、窗口尺寸、叠加层状态）。</summary>
    public void RefreshImage()
    {
        Img.Source = Sticker.Image;
        Width = Sticker.W;
        Height = Sticker.H;
        if (Sticker.OcrWords is null)
            OverlayLayer.Visibility = Visibility.Collapsed;
        RefreshFrame();
    }

    /// <summary>是否处于标注模式（管理器钩子 / 菜单判断用）。</summary>
    public bool IsAnnotating => _annotating;

    protected override void OnClosed(EventArgs e)
    {
        _hwndSource?.RemoveHook(_hook);
        _floating?.Close();   // 独立浮动条随贴图关闭（Owner 机制也会关，双保险）
        _annotBar?.Close();   // 标注工具条同理
        _owner.OnWindowClosed(this);
        base.OnClosed(e);
    }
}
