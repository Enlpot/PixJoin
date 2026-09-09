using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Linq;
using System.Windows.Media.Effects;
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

    // ---- 缩略图模式（PixPin：右键框选显示范围，不裁剪原图；Shift+左键平移内容；菜单裁剪才真裁剪） ----
    private Rect? _thumbRect;          // 图片像素坐标的显示范围；null = 完整显示
    private Rect _preThumbGeo;         // 进入缩略图前的窗口几何 (X,Y,W,H)
    private Point? _thumbDragStart;    // 右键框选起点（窗口 DIP）
    private bool _thumbDragging;       // 右键框选进行中
    private Point? _thumbPanStart;     // Shift+左键平移起点（原图像素）
    private Rect? _thumbPanRect;       // 平移起点时的显示范围
    private bool _thumbPanning;        // 平移进行中
    private readonly Rectangle _thumbSel = new()
    {
        IsHitTestVisible = false,
        Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x29, 0x9D, 0xF0)),
        StrokeThickness = 1.5,
        Fill = new SolidColorBrush(Color.FromArgb(0x33, 0x29, 0x9D, 0xF0)),
    };
    private bool _textSelectable = true;         // 贴图级「文本可选择」开关（右键菜单）
    private readonly SolidColorBrush _selectBrush = new(Color.FromArgb(0x55, 0x33, 0x88, 0xFF));
    private FloatingBarWindow? _floating;        // 文字选择浮动工具条（独立小窗口，可超出贴图窗口）

    // ---- 标注 ----
    private AnnotationBarWindow? _annotBar;
    private readonly List<Annotation> _annotations = new();
    private AnnotationTool? _annotTool;          // null=未选工具（贴图可拖动）
    private Color _annotColor = Color.FromRgb(0xE5, 0x39, 0x35);
    private double _annotThickness = 4;
    private bool _annotating;                    // 标注模式
    private bool _annotDrawing;                  // 正在拖动绘制
    private Point _annotStart;                   // 拖动起点（窗口 DIP）
    private Point _annotLast;                    // 当前点（窗口 DIP）
    private List<double> _annotPenPts = new();
    private int? _annotSelIndex;             // 选中的标注索引（null=无选中；与截图侧一致）
    private AnnotHandle? _annotDragHandle;   // 正在拖动的控制点
    private bool _annotDraggingBody;         // 正在拖动标注本体
    private Point _annotDragOffset;          // 本体拖动锚点（物理像素，相对标注 X/Y）
    private double _annotArrowT = 0.5;       // 箭头弧顶控制点在曲线上的锁定参数 t
    private bool _annotDashed;                // 默认线型（虚线开关，作用于新标注/选中标注，与截图一致）
    private ArrowStyle _annotArrowStyle = ArrowStyle.Solid;   // 默认箭头样式

    // ---- 裁剪模式（贴图内交互裁剪：遮罩 + 手柄拖动，Enter/双击确认，Esc 取消） ----
    private bool _cropMode;
    private Rect _cropRect;                   // 裁剪框（窗口 DIP）
    private enum CropDrag { None, Move, Corner, Edge }
    private CropDrag _cropDrag = CropDrag.None;
    private int _cropDragIndex;
    private Point _cropStart;                 // 拖动起点（DIP）
    private Rect _cropOrig;                   // 拖动前裁剪框
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
        PreviewMouseRightButtonDown += OnRightButtonDown;
        MouseWheel += OnMouseWheel;

        // 中键 = 重置为原始大小（PixPin 同款）；裁剪 / 标注 / 锁定中禁用
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            if (_cropMode || _annotating || Sticker.IsLocked) { e.Handled = true; return; }
            ResetToOriginalSize();
            e.Handled = true;
        };

        ApplyShadow(Sticker.HasShadow);

        // 拖入图片文件 → 直接贴图（PixPin 同款交互）
        AllowDrop = true;
        DragOver += OnBodyDragOver;
        Drop += OnBodyDrop;

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

    private static bool HasDroppableImage(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] files)
            return false;
        foreach (var f in files)
        {
            string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp") return true;
        }
        return false;
    }

    private void OnBodyDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasDroppableImage(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnBodyDrop(object sender, DragEventArgs e)
    {
        if (!HasDroppableImage(e.Data)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        Win32.GetPhysicalCursorPos(out var p);
        _owner.PinFiles(files, new Point(p.X, p.Y));
        e.Handled = true;
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

        // 裁剪模式：拖动裁剪框
        if (_cropMode)
        {
            HandleCropDown(e.GetPosition(this));
            e.Handled = true;
            return;
        }

        // 缩略图模式：Shift+左键 = 平移显示内容（不移动贴图）
        if (_thumbRect is not null && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            if (Sticker.IsLocked) { e.Handled = true; return; }
            ClearTextSelection();
            _thumbPanning = true;
            _thumbPanStart = ToOrigImageCoord(e.GetPosition(this));
            _thumbPanRect = _thumbRect;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        // 标注模式：未选工具时左键=拖动贴图；选中工具才绘制标注
        if (_annotating)
        {
            if (_annotTool is null)
            {
                ClearTextSelection();
                CaptureMouse();
                _owner.BeginDrag(this, e.GetPosition(this));
                e.Handled = true;
                return;
            }
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
        // 缩略图框选（右键按住拖动）
        if (_thumbDragStart is { } ts && !_cropMode && !_annotating && !Sticker.IsLocked
            && e.RightButton == MouseButtonState.Pressed)
        {
            var cur = e.GetPosition(this);
            if (!_thumbDragging && (Math.Abs(cur.X - ts.X) > 4 || Math.Abs(cur.Y - ts.Y) > 4))
            {
                _thumbDragging = true;
                OverlayLayer.Visibility = Visibility.Visible;
            }
            if (_thumbDragging)
            {
                UpdateThumbDrag(ts, cur);
                e.Handled = true;
                return;
            }
        }

        // 缩略图内容平移（Shift+左键）
        if (_thumbPanning)
        {
            UpdateThumbPan(e.GetPosition(this));
            e.Handled = true;
            return;
        }

        if (_cropMode)
        {
            HandleCropMove(e.GetPosition(this));
            return;
        }

        if (_annotating)
        {
            if (_annotTool is null)
            {
                Cursor = Cursors.Arrow;
                if (_owner.IsDragging) _owner.UpdateDrag(this);
                return;
            }
            Cursor = Cursors.Cross;
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
        if (_cropMode)
        {
            HandleCropUp(e.GetPosition(this));
            e.Handled = true;
            return;
        }

        if (_thumbPanning)
        {
            _thumbPanning = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (_annotating)
        {
            if (_annotTool is null)
            {
                if (_owner.IsDragging)
                {
                    if (IsMouseCaptured) ReleaseMouseCapture();
                    _owner.EndDrag(this);
                    e.Handled = true;
                    PositionAnnotationBar();   // 贴图移动后工具条跟随重定位
                }
                return;
            }
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

    private void OnRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 标注 / 裁剪 / 锁定 时不进入缩略图框选
        if (_annotating || _cropMode || Sticker.IsLocked) return;
        _thumbDragStart = e.GetPosition(this);
        _thumbDragging = false;
        e.Handled = true;
    }

    private void OnRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 标注模式：右键不弹菜单（避免误操作），Esc / ✓ 完成退出
        if (_annotating) { e.Handled = true; return; }

        // 右键框选缩略图：拖动过则应用框选，未拖动才弹菜单
        if (_thumbDragStart is not null)
        {
            _thumbDragStart = null;
            if (_thumbDragging)
            {
                _thumbDragging = false;
                ApplyThumbDrag(e.GetPosition(this));
                e.Handled = true;
                return;
            }
        }

        var menu = BuildContextMenu();
        menu.IsOpen = true;
        e.Handled = true;
    }

    // ---------------- 缩略图模式 ----------------

    /// <summary>窗口 DIP → 原图像素（缩略图模式下叠加显示范围偏移）。</summary>
    private Point ToOrigImageCoord(Point dip)
    {
        if (_thumbRect is { } tr)
        {
            double iw = Img.ActualWidth > 0 ? Img.ActualWidth : tr.Width;
            double ih = Img.ActualHeight > 0 ? Img.ActualHeight : tr.Height;
            return new Point(tr.X + dip.X * tr.Width / iw, tr.Y + dip.Y * tr.Height / ih);
        }
        return ToImageCoord(dip);
    }

    private void UpdateThumbDrag(Point start, Point cur)
    {
        var r = new Rect(Math.Min(start.X, cur.X), Math.Min(start.Y, cur.Y),
                         Math.Abs(cur.X - start.X), Math.Abs(cur.Y - start.Y));
        _thumbSel.Width = r.Width;
        _thumbSel.Height = r.Height;
        Canvas.SetLeft(_thumbSel, r.X);
        Canvas.SetTop(_thumbSel, r.Y);
        if (!OverlayLayer.Children.Contains(_thumbSel))
            OverlayLayer.Children.Add(_thumbSel);
    }

    private void ClearThumbSel()
    {
        OverlayLayer.Children.Remove(_thumbSel);
        OverlayLayer.Visibility = Sticker.OcrWords is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>应用右键框选为缩略图显示范围（不裁剪原图，保持原图清晰度）。</summary>
    private void ApplyThumbDrag(Point cur)
    {
        ClearThumbSel();
        if (_thumbDragStart is not { } ts) return;
        var a = ToOrigImageCoord(ts);
        var b = ToOrigImageCoord(cur);
        var px = new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                          Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        if (px.Width < 8 || px.Height < 8) return;   // 太小视为误操作
        ApplyThumbnail(px);
    }

    /// <summary>进入缩略图模式：窗口等比缩到框选范围，原图保持不动（CroppedBitmap 视图）。</summary>
    private void ApplyThumbnail(Rect px)
    {
        int pw = Sticker.Image.PixelWidth;
        int ph = Sticker.Image.PixelHeight;
        px.Intersect(new Rect(0, 0, pw, ph));
        if (px.Width < 8 || px.Height < 8) return;

        int x = (int)Math.Round(px.X), y = (int)Math.Round(px.Y);
        int w = (int)Math.Round(px.Width), h = (int)Math.Round(px.Height);
        x = Math.Clamp(x, 0, pw - w);
        y = Math.Clamp(y, 0, ph - h);
        if (x + w > pw) w = pw - x;
        if (y + h > ph) h = ph - y;
        if (w < 8 || h < 8) return;

        _preThumbGeo = new Rect(Sticker.X, Sticker.Y, Sticker.W, Sticker.H);
        _thumbRect = new Rect(x, y, w, h);

        var crop = new CroppedBitmap(Sticker.Image, new Int32Rect(x, y, w, h));
        crop.Freeze();
        Img.Source = crop;

        double fw = (double)w / pw, fh = (double)h / ph;
        Sticker.W = Math.Max(16, Sticker.W * fw);
        Sticker.H = Math.Max(16, Sticker.H * fh);
        ApplyGeometry();
        ClearTextSelection();
        _owner.OnStickerResized(this);
    }

    /// <summary>恢复完整显示：还原原图与进入缩略图前的窗口几何。</summary>
    private void ExitThumbnail()
    {
        if (_thumbRect is null) return;
        _thumbRect = null;
        Img.Source = Sticker.Image;
        Sticker.X = _preThumbGeo.X;
        Sticker.Y = _preThumbGeo.Y;
        Sticker.W = _preThumbGeo.Width;
        Sticker.H = _preThumbGeo.Height;
        ApplyGeometry();
        _owner.OnStickerResized(this);
    }

    /// <summary>按缩略图范围真正裁剪原图（破坏性，可撤销一次）。</summary>
    private void CropToThumbnail()
    {
        if (_thumbRect is not { } tr) return;
        Sticker.UndoImage = Sticker.Image;
        var crop = new CroppedBitmap(Sticker.Image,
            new Int32Rect((int)Math.Round(tr.X), (int)Math.Round(tr.Y),
                          (int)Math.Round(tr.Width), (int)Math.Round(tr.Height)));
        crop.Freeze();
        Sticker.Image = crop;
        Sticker.OcrWords = null;   // 词框坐标失效
        _thumbRect = null;
        Img.Source = crop;
        _preThumbGeo = new Rect(Sticker.X, Sticker.Y, Sticker.W, Sticker.H);
        ApplyGeometry();
        ClearTextSelection();
        _owner.OnStickerResized(this);
    }

    /// <summary>Shift+左键平移显示内容：显示范围在图片内移动，窗口不动。</summary>
    private void UpdateThumbPan(Point dip)
    {
        if (_thumbPanStart is not { } ps || _thumbPanRect is not { } pr) return;
        var cur = ToOrigImageCoord(dip);
        double dx = cur.X - ps.X, dy = cur.Y - ps.Y;
        int pw = Sticker.Image.PixelWidth, ph = Sticker.Image.PixelHeight;
        double nx = Math.Clamp(pr.X + dx, 0, Math.Max(0, pw - pr.Width));
        double ny = Math.Clamp(pr.Y + dy, 0, Math.Max(0, ph - pr.Height));
        if (Math.Abs(nx - pr.X) < 0.01 && Math.Abs(ny - pr.Y) < 0.01) return;
        _thumbRect = new Rect(nx, ny, pr.Width, pr.Height);
        var crop = new CroppedBitmap(Sticker.Image,
            new Int32Rect((int)Math.Round(nx), (int)Math.Round(ny),
                          (int)Math.Round(pr.Width), (int)Math.Round(pr.Height)));
        crop.Freeze();
        Img.Source = crop;
    }

    private void OnCreateGroup()
    {
        var dlg = new InputDialog("新建贴图分组", "分组名称：", "");
        if (dlg.ShowDialog() != true) return;
        var name = dlg.Value.Trim();
        if (string.IsNullOrEmpty(name)) return;
        _owner.GroupSticker(this, name);
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 裁剪模式：滚轮不缩放
        if (_cropMode) { e.Handled = true; return; }

        // 标注模式：滚轮只调整选中标注的粗细（±1，clamp 1~20），与截图一致
        if (_annotating)
        {
            if (_annotSelIndex is { } si && si >= 0 && si < _annotations.Count)
            {
                var a = _annotations[si];
                double nt = Math.Clamp(a.Thickness + (e.Delta > 0 ? 1 : -1), 1, 20);
                if (Math.Abs(nt - a.Thickness) > 0.01)
                {
                    _annotations[si] = AnnotationHandles.Rebuild(a, thickness: nt);
                    RefreshAnnotationLayer();
                }
            }
            e.Handled = true;
            return;
        }

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

        var copyAsFile = new MenuItem { Header = "复制为文件", IsEnabled = !Sticker.IsLocked };
        copyAsFile.Click += (_, _) => _owner.CopyStickerAsFile(this);
        menu.Items.Add(copyAsFile);

        var openExt = new MenuItem { Header = "用默认程序打开" };
        openExt.Click += (_, _) => _owner.OpenStickerExternally(this);
        menu.Items.Add(openExt);

        menu.Items.Add(new Separator());

        var detach = new MenuItem { Header = "拆离此贴图", IsEnabled = grouped };
        detach.Click += (_, _) => _owner.DetachSticker(this);
        menu.Items.Add(detach);

        var dissolve = new MenuItem { Header = "全部解散", IsEnabled = grouped };
        dissolve.Click += (_, _) => _owner.DissolveGroup(this);
        menu.Items.Add(dissolve);

        menu.Items.Add(new Separator());

        // ---- 缩略图模式 ----
        if (_thumbRect is not null)
        {
            var exitThumb = new MenuItem { Header = "恢复完整显示" };
            exitThumb.Click += (_, _) => ExitThumbnail();
            menu.Items.Add(exitThumb);

            var cropThumb = new MenuItem { Header = "裁剪为当前区域", IsEnabled = !Sticker.IsLocked };
            cropThumb.Click += (_, _) => CropToThumbnail();
            menu.Items.Add(cropThumb);

            menu.Items.Add(new Separator());
        }

        // ---- OCR / 文字识别 ----
        bool hasOcr = Sticker.OcrWords is { Count: > 0 };
        var copyAllText = new MenuItem { Header = "复制所有文本（Shift+C）", IsEnabled = hasOcr };
        copyAllText.Click += (_, _) => _owner.CopyAllOcrText(this);
        menu.Items.Add(copyAllText);

        var copyTable = new MenuItem { Header = "复制为表格（CSV）", IsEnabled = hasOcr };
        copyTable.Click += (_, _) => _owner.CopyOcrTable(this);
        menu.Items.Add(copyTable);

        var copyMdTable = new MenuItem { Header = "复制为 Markdown 表格", IsEnabled = hasOcr };
        copyMdTable.Click += (_, _) => _owner.CopyOcrMarkdownTable(this);
        menu.Items.Add(copyMdTable);

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
        var adjust = new MenuItem { Header = "亮度 / 对比度 / 饱和度…" };
        adjust.Click += (_, _) => ImageOpDialogs.ShowAdjust(this);
        imgOps.Items.Add(adjust);
        var border = new MenuItem { Header = "添加边框…" };
        border.Click += (_, _) => ImageOpDialogs.ShowBorder(this);
        imgOps.Items.Add(border);
        var watermark = new MenuItem { Header = "文字水印…" };
        watermark.Click += (_, _) => ImageOpDialogs.ShowWatermark(this);
        imgOps.Items.Add(watermark);
        imgOps.Items.Add(new Separator());
        AddImageOp(imgOps, "旋转 90°", b => ImageProcessor.Rotate(b, 90));
        AddImageOp(imgOps, "旋转 180°", b => ImageProcessor.Rotate(b, 180));
        AddImageOp(imgOps, "旋转 270°", b => ImageProcessor.Rotate(b, 270));
        imgOps.Items.Add(new Separator());
        AddImageOp(imgOps, "水平翻转", ImageProcessor.FlipHorizontal);
        AddImageOp(imgOps, "垂直翻转", ImageProcessor.FlipVertical);
        imgOps.Items.Add(new Separator());
        var cropItem = new MenuItem { Header = "裁剪…", IsEnabled = !Sticker.IsLocked };
        cropItem.Click += (_, _) => EnterCropMode();
        imgOps.Items.Add(cropItem);
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

        var shadow = new MenuItem { Header = "阴影 (Y)", IsChecked = Sticker.HasShadow };
        shadow.Click += (_, _) => ApplyShadow(!Sticker.HasShadow);
        menu.Items.Add(shadow);

        // 贴图分组
        var groupRoot = new MenuItem { Header = "贴图分组" };
        var inGroup = !string.IsNullOrEmpty(Sticker.GroupName);
        var ungroup = new MenuItem { Header = "取消分组", IsEnabled = inGroup };
        ungroup.Click += (_, _) => _owner.UngroupSticker(this);
        groupRoot.Items.Add(ungroup);
        foreach (var g in _owner.GetGroupNames().Where(n => n != Sticker.GroupName))
        {
            var gname = g;
            var gi = new MenuItem { Header = $"加入分组「{gname}」" };
            gi.Click += (_, _) => _owner.GroupSticker(this, gname);
            groupRoot.Items.Add(gi);
        }
        var newGroup = new MenuItem { Header = "新建分组…" };
        newGroup.Click += (_, _) => OnCreateGroup();
        groupRoot.Items.Add(newGroup);
        menu.Items.Add(groupRoot);

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
    /// <summary>图像处理参数弹窗的实时预览应用。</summary>
    public void ApplyPreviewOp(Func<BitmapSource, BitmapSource> op) => _owner.ApplyImageOp(this, op);

    /// <summary>参数弹窗"重置"：恢复打开时原图。</summary>
    public void RestorePreviewImage(BitmapSource orig) => _owner.RestorePreviewImage(this, orig);

    private void AddImageOp(MenuItem root, string header, Func<BitmapSource, BitmapSource> op)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => _owner.ApplyImageOp(this, op);
        root.Items.Add(item);
    }
    /// <summary>鼠标穿透（托盘全局开关）：点击事件穿过贴图到下层窗口。</summary>
    /// <summary>恢复尺寸到图片原始物理像素（中键）。</summary>
    public void ResetToOriginalSize()
    {
        int pw = Sticker.Image.PixelWidth;
        int ph = Sticker.Image.PixelHeight;
        if (pw < 1 || ph < 1) return;
        if (Math.Abs(Sticker.W - pw) < 0.01 && Math.Abs(Sticker.H - ph) < 0.01) return;
        ClearTextSelection();
        Sticker.W = pw;
        Sticker.H = ph;
        ApplyGeometry();
        _owner.OnStickerResized(this);
    }

    /// <summary>贴图阴影开关：DropShadowEffect 挂在外框上（透明窗口需 AllowsTransparency）。</summary>
    public void ApplyShadow(bool on)
    {
        Sticker.HasShadow = on;
        Frame.Effect = on
            ? new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.5, Color = Colors.Black }
            : null;
    }

    /// <summary>恢复尺寸（供管理器恢复关闭快照）。</summary>
    public void SetSize(double w, double h)
    {
        Sticker.W = Math.Max(16, w);
        Sticker.H = Math.Max(16, h);
        ApplyGeometry();
        _owner.OnStickerResized(this);
    }

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
        _annotTool = null;          // 进入标注不默认选中工具
        ClearTextSelection();
        _annotSelIndex = null;
        _annotDragHandle = null;
        _annotDraggingBody = false;
        _annotArrowT = 0.5;

        if (_annotBar is null)
        {
            _annotBar = new AnnotationBarWindow();
            _annotBar.Owner = this;
            _annotBar.ToolChanged += t => _annotTool = t;
            _annotBar.ColorChanged += c => _annotColor = c;
            _annotBar.ThicknessChanged += t => _annotThickness = t;
            _annotBar.UndoRequested += UndoAnnotationStep;
            _annotBar.DoneRequested += () => ExitAnnotationMode(commit: true);
            _annotBar.StyleApplied += (s, d) =>
            {
                _annotArrowStyle = s;
                _annotDashed = d;
                if (_annotSelIndex is { } si && si >= 0 && si < _annotations.Count)
                    _annotations[si] = AnnotationHandles.Rebuild(_annotations[si], dashed: d, arrow: s);
                RefreshAnnotationLayer();
            };
        }
        PositionAnnotationBar();
        _annotBar.Show();

        OverlayLayer.Visibility = Visibility.Visible;
        AnnotationLayer.Visibility = Visibility.Visible;
        HandleLayer.Visibility = Visibility.Collapsed;
        Cursor = Cursors.Arrow;   // 未选工具时可拖动贴图
    }

    /// <summary>退出标注模式。commit=true 时把标注固化进图片（可用右键「撤销标注」回退）。</summary>
    public void ExitAnnotationMode(bool commit)
    {
        if (!_annotating) return;
        _annotating = false;
        _annotDrawing = false;
        _annotSelIndex = null;
        _annotDragHandle = null;
        _annotDraggingBody = false;
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
        _annotSelIndex = _annotations.Count > 0 ? Math.Min(_annotSelIndex ?? int.MaxValue, _annotations.Count - 1) : null;
        RefreshAnnotationLayer();
    }

    private void PositionAnnotationBar()
    {
        if (_annotBar is null) return;
        _annotBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double bw = _annotBar.DesiredSize.Width;
        double bh = _annotBar.DesiredSize.Height;
        double vsL = SystemParameters.VirtualScreenLeft;
        double vsT = SystemParameters.VirtualScreenTop;
        double vsR = vsL + SystemParameters.VirtualScreenWidth;
        double vsB = vsT + SystemParameters.VirtualScreenHeight;

        // 首选：工具条右上角 = 贴图右下角
        double x = Left + ActualWidth - bw;
        double y = Top + ActualHeight;
        bool fits = x >= vsL && y + bh <= vsB;
        if (!fits)
        {
            // 回退1：贴图右侧垂直居中
            x = Left + ActualWidth + 8;
            y = Math.Max(vsT, Math.Min(Top + ActualHeight - bh / 2, vsB - bh));
            fits = x + bw <= vsR && y >= vsT && y + bh <= vsB;
        }
        if (!fits)
        {
            // 回退2：贴图上方（右上角对齐贴图右上角）
            x = Left + ActualWidth - bw;
            y = Top - bh - 8;
            fits = x >= vsL && y >= vsT;
        }
        if (!fits)
        {
            x = Math.Max(vsL, Math.Min(x, vsR - bw));
            y = Math.Max(vsT, Math.Min(y, vsB - bh));
        }
        _annotBar.Left = x;
        _annotBar.Top = y;
    }

    private void HandleAnnotationDown(Point p)
    {
        var ip = ToImageCoord(p);

        // 1. 已选中标注：先命中控制点 → 直接拖控制点（角/边=拉伸、圆角=调弧度、箭头=调三点）
        if (_annotSelIndex is { } sel && sel >= 0 && sel < _annotations.Count)
        {
            var hh = AnnotationHandles.HitTest(AnnotationHandles.Get(_annotations[sel], _annotArrowT), ip);
            if (hh is not null)
            {
                _annotDragHandle = hh;
                _annotDrawing = false;
                CaptureMouse();
                RefreshAnnotationLayer();
                return;
            }
        }

        // 2. 命中已有标注（从新到旧，精确命中）→ 选中并拖动本体
        for (int i = _annotations.Count - 1; i >= 0; i--)
        {
            if (AnnotationHandles.HitShape(_annotations[i], ip, 5))
            {
                _annotSelIndex = i;
                _annotDraggingBody = true;
                var a = _annotations[i];
                _annotDragOffset = new Point(ip.X - a.X, ip.Y - a.Y);
                _annotArrowT = 0.5;
                CaptureMouse();
                RefreshAnnotationLayer();
                return;
            }
        }

        // 3. 空白：取消选中，开始新绘制（与截图侧一致）
        _annotSelIndex = null;

        if (_annotTool == AnnotationTool.Text || _annotTool == AnnotationTool.Number)
        {
            // 落点工具：立即生成元素，不进入拖动
            if (_annotTool == AnnotationTool.Text)
            {
                var dlg = new TextInputWindow { Owner = this };
                if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.ResultText))
                {
                    _annotations.Add(new Annotation
                    {
                        Tool = AnnotationTool.Text, X = ip.X, Y = ip.Y,
                        Text = dlg.ResultText, Color = _annotColor, Thickness = _annotThickness,
                    });
                    _annotSelIndex = _annotations.Count - 1;
                    RefreshAnnotationLayer();
                }
            }
            else
            {
                _annotNumberSeq++;
                _annotations.Add(new Annotation
                {
                    Tool = AnnotationTool.Number, X = ip.X, Y = ip.Y,
                    Number = _annotNumberSeq, Color = _annotColor, Thickness = _annotThickness,
                });
                _annotSelIndex = _annotations.Count - 1;
                RefreshAnnotationLayer();
            }
            return;
        }

        _annotDrawing = true;
        _annotStart = p;
        _annotLast = p;
        _annotPenPts.Clear();
        _annotPenPts.Add(ip.X);
        _annotPenPts.Add(ip.Y);
        CaptureMouse();
        RefreshAnnotationLayer();
    }

    private void HandleAnnotationMove(Point p)
    {
        var ip = ToImageCoord(p);

        // 拖控制点（与截图侧同一套变形公式）
        if (_annotDragHandle is { } hh && _annotSelIndex is { } si && si >= 0 && si < _annotations.Count)
        {
            var updated = AnnotationHandles.ApplyDrag(_annotations[si], hh, ip, _annotArrowT);
            if (updated is not null)
            {
                _annotations[si] = updated;
                RefreshAnnotationLayer();
            }
            return;
        }

        // 拖标注本体（帧增量，保证 1:1 跟手）
        if (_annotDraggingBody && _annotSelIndex is { } sib && sib >= 0 && sib < _annotations.Count)
        {
            var a = _annotations[sib];
            var delta = new Point(ip.X - _annotDragOffset.X - a.X, ip.Y - _annotDragOffset.Y - a.Y);
            if (Math.Abs(delta.X) > 0.01 || Math.Abs(delta.Y) > 0.01)
            {
                _annotations[sib] = AnnotationHandles.Move(a, delta);
                RefreshAnnotationLayer();
            }
            return;
        }

        if (!_annotDrawing) return;
        _annotLast = p;
        if (_annotTool == AnnotationTool.Pen)
        {
            _annotPenPts.Add(ip.X);
            _annotPenPts.Add(ip.Y);
        }
        RefreshAnnotationLayer();
    }

    private void HandleAnnotationUp(Point p)
    {
        // 拖控制点 / 拖本体结束
        if (_annotDragHandle is not null || _annotDraggingBody)
        {
            _annotDragHandle = null;
            _annotDraggingBody = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            RefreshAnnotationLayer();
            return;
        }

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
            case AnnotationTool.Ellipse:
            case AnnotationTool.Spotlight:
            case AnnotationTool.Magnifier:
                _annotations.Add(new Annotation
                {
                    Tool = _annotTool!.Value,
                    X = Math.Min(s.X, e.X), Y = Math.Min(s.Y, e.Y),
                    W = Math.Abs(e.X - s.X), H = Math.Abs(e.Y - s.Y),
                    Color = _annotColor, Thickness = _annotThickness,
                });
                break;

            case AnnotationTool.Arrow:
                _annotations.Add(new Annotation
                {
                    Tool = AnnotationTool.Arrow,
                    Points = new[] { s.X, s.Y, (s.X + e.X) / 2, (s.Y + e.Y) / 2, e.X, e.Y },
                    Color = _annotColor, Thickness = _annotThickness,
                });
                break;

            case AnnotationTool.Line:
            case AnnotationTool.Curve:
                _annotations.Add(new Annotation
                {
                    Tool = _annotTool!.Value,
                    Points = new[] { s.X, s.Y, e.X, e.Y },
                    Color = _annotColor, Thickness = _annotThickness,
                });
                break;

            case AnnotationTool.Pen:
            case AnnotationTool.Polyline:
                if (_annotPenPts.Count >= 4)
                {
                    _annotations.Add(new Annotation
                    {
                        Tool = _annotTool!.Value, Points = _annotPenPts.ToArray(),
                        Color = _annotColor, Thickness = _annotThickness,
                    });
                }
                _annotPenPts.Clear();
                break;
        }

        _annotSelIndex = _annotations.Count - 1;
        RefreshAnnotationLayer();
    }

    /// <summary>把全部标注 + 进行中元素画到预览层（DIP 坐标，物理像素按显示缩放比换算）。</summary>
    private void RefreshAnnotationLayer()
    {
        AnnotationLayer.Children.Clear();
        foreach (var a in _annotations) AddAnnotationPreview(a);
        // 选中标注：画选中框 + 控制点（与截图侧完全一致；绘制中隐藏）
        if (!_annotDrawing && _annotSelIndex is { } sel && sel >= 0 && sel < _annotations.Count)
        {
            double hsx = Img.ActualWidth / Sticker.Image.PixelWidth;
            double hsy = Img.ActualHeight / Sticker.Image.PixelHeight;
            AnnotationPainter.DrawHandles(AnnotationLayer, _annotations[sel], hsx, hsy, _annotArrowT);
        }
        DrawFloatBar();
        if (_annotDrawing && _annotTool is { } tool)
        {
            var s = ToImageCoord(_annotStart);
            var e = ToImageCoord(_annotLast);
            Annotation? tmp = null;
            switch (tool)
            {
                case AnnotationTool.Rect:
                case AnnotationTool.Mosaic:
                case AnnotationTool.Ellipse:
                case AnnotationTool.Highlight:
                case AnnotationTool.Spotlight:
                case AnnotationTool.Magnifier:
                    tmp = new Annotation
                    {
                        Tool = tool, Color = _annotColor, Thickness = _annotThickness,
                        X = Math.Min(s.X, e.X), Y = Math.Min(s.Y, e.Y),
                        W = Math.Abs(e.X - s.X), H = Math.Abs(e.Y - s.Y),
                    };
                    break;
                case AnnotationTool.Arrow:
                    tmp = new Annotation
                    {
                        Tool = tool, Color = _annotColor, Thickness = _annotThickness,
                        Points = new[] { s.X, s.Y, (s.X + e.X) / 2, (s.Y + e.Y) / 2, e.X, e.Y },
                    };
                    break;
                case AnnotationTool.Line:
                case AnnotationTool.Curve:
                    tmp = new Annotation
                    {
                        Tool = tool, Color = _annotColor, Thickness = _annotThickness,
                        Points = new[] { s.X, s.Y, e.X, e.Y },
                    };
                    break;
                case AnnotationTool.Pen:
                case AnnotationTool.Polyline:
                    tmp = new Annotation
                    {
                        Tool = tool, Color = _annotColor, Thickness = _annotThickness,
                        Points = _annotPenPts.ToArray(),
                    };
                    break;
            }
            if (tmp is not null) AddAnnotationPreview(tmp);
        }
    }

    /// <summary>
    /// 选中标注浮动条（样式▾ + 8 色 + 粗细 −/数字/+ + 删除），与截图侧一致。
    /// 画在标注层最上方；位于标注上方，放不下时移到下方。
    /// </summary>
    private void DrawFloatBar()
    {
        if (_annotSelIndex is not { } si || si < 0 || si >= _annotations.Count) return;
        var a = _annotations[si];
        double sx = Img.ActualWidth / Sticker.Image.PixelWidth;
        double sy = Img.ActualHeight / Sticker.Image.PixelHeight;
        var selColor = a.Color;

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x22, 0x22, 0x22)) };

        // 样式下拉（箭头=样式+线型；其余=线型），作用于选中标注
        if (a.Tool != AnnotationTool.Highlight && a.Tool != AnnotationTool.Mosaic)
        {
            var styleBtn = new Button
            {
                Content = new TextBlock { Text = "▾", Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(2, 0, 2, 0) },
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(5, 1, 5, 1),
                Cursor = Cursors.Arrow,
                ToolTip = "样式（线型/箭头形状）",
            };
            var styleMenu = a.Tool == AnnotationTool.Arrow
                ? AnnotationStyleMenus.BuildArrowMenu(_annotArrowStyle, _annotDashed, ApplyAnnotStyle)
                : AnnotationStyleMenus.BuildLineStyleMenu(_annotDashed, ApplyAnnotStyle);
            styleBtn.Click += (_, _) => { styleMenu.PlacementTarget = styleBtn; styleMenu.IsOpen = true; };
            panel.Children.Add(styleBtn);
        }

        foreach (var cc in AnnotationStyleMenus.Colors)
        {
            bool active = cc == selColor;
            var cb = new Border
            {
                Width = 14, Height = 14,
                Background = new SolidColorBrush(cc),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(2),
                BorderBrush = active ? Brushes.White : Brushes.Transparent,
                BorderThickness = new Thickness(active ? 2 : 1),
                Cursor = Cursors.Hand,
                Tag = cc,
            };
            var target = si;
            cb.MouseLeftButtonDown += (_, _) =>
            {
                _annotations[target] = AnnotationHandles.Rebuild(_annotations[target], color: (Color)cb.Tag);
                RefreshAnnotationLayer();
            };
            panel.Children.Add(cb);
        }

        var minus = MakeFloatBtn("−");
        minus.Click += (_, _) => AdjustAnnotThickness(-1);
        panel.Children.Add(minus);

        var num = new TextBlock
        {
            Text = $"{a.Thickness:0.#}",
            Foreground = Brushes.White,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 2, 0),
        };
        panel.Children.Add(num);

        var plus = MakeFloatBtn("+");
        plus.Click += (_, _) => AdjustAnnotThickness(1);
        panel.Children.Add(plus);

        var del = MakeFloatBtn("✕");
        del.Click += (_, _) =>
        {
            if (_annotSelIndex is { } dsi && dsi >= 0 && dsi < _annotations.Count)
            {
                _annotations.RemoveAt(dsi);
                _annotSelIndex = null;
                RefreshAnnotationLayer();
            }
        };
        panel.Children.Add(del);

        var host = new Border
        {
            Child = panel,
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x22, 0x22, 0x22)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(3),
        };

        var b = AnnotationHandles.Bounds(a);
        // 与截图侧一致：浮动样式条对齐标注工具条左端下方（水平跟随工具条）
        double fx, fy;
        if (_annotBar is { IsVisible: true })
        {
            fx = _annotBar.Left - Left;
            fy = _annotBar.Top + _annotBar.Height + 8 - Top;
        }
        else
        {
            fx = (b.X + b.Width / 2) * sx;
            fy = b.Y * sy - 34;
        }
        if (fy < 0) fy = (b.Y + b.Height) * sy + 8;
        Canvas.SetLeft(host, fx);
        Canvas.SetTop(host, fy);
        AnnotationLayer.Children.Add(host);
    }

    private Button MakeFloatBtn(string glyph) => new()
    {
        Content = new TextBlock { Text = glyph, Foreground = Brushes.White, FontSize = 12 },
        Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
        BorderThickness = new Thickness(0),
        Padding = new Thickness(5, 1, 5, 1),
        Margin = new Thickness(1, 0, 1, 0),
        Cursor = Cursors.Arrow,
    };

    private void AdjustAnnotThickness(double delta)
    {
        if (_annotSelIndex is not { } si || si < 0 || si >= _annotations.Count) return;
        var a = _annotations[si];
        double nt = Math.Clamp(a.Thickness + delta, 1, 20);
        if (Math.Abs(nt - a.Thickness) > 0.01)
        {
            _annotations[si] = AnnotationHandles.Rebuild(a, thickness: nt);
            RefreshAnnotationLayer();
        }
    }

    private void ApplyAnnotStyle(bool dashed)
    {
        _annotDashed = dashed;
        if (_annotSelIndex is { } si && si >= 0 && si < _annotations.Count)
            _annotations[si] = AnnotationHandles.Rebuild(_annotations[si], dashed: dashed);
        RefreshAnnotationLayer();
    }

    private void ApplyAnnotStyle(ArrowStyle style, bool dashed)
    {
        _annotArrowStyle = style;
        _annotDashed = dashed;
        if (_annotSelIndex is { } si && si >= 0 && si < _annotations.Count)
            _annotations[si] = AnnotationHandles.Rebuild(_annotations[si], dashed: dashed, arrow: style);
        RefreshAnnotationLayer();
    }

    /// <summary>物理像素 → 窗口 DIP（用于预览层定位）。</summary>
    private Point ToDip(Point phys) => new(
        phys.X * Img.ActualWidth / Sticker.Image.PixelWidth,
        phys.Y * Img.ActualHeight / Sticker.Image.PixelHeight);

    private void AddAnnotationPreview(Annotation a)
    {
        double sx = Img.ActualWidth / Sticker.Image.PixelWidth;
        double sy = Img.ActualHeight / Sticker.Image.PixelHeight;
        // 与截图侧同一渲染器，保证两种入口标注外观完全一致
        AnnotationPainter.Draw(AnnotationLayer, a, sx, sy,
            new Size(AnnotationLayer.ActualWidth, AnnotationLayer.ActualHeight), Sticker.Image);
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

    /// <summary>进入裁剪模式：显示遮罩 + 裁剪框（默认全图），禁标注/拖动。</summary>
    public void EnterCropMode()
    {
        if (_cropMode || Sticker.IsLocked) return;
        if (_annotating) ExitAnnotationMode(commit: true);
        ClearTextSelection();
        _cropMode = true;
        _cropDrag = CropDrag.None;
        _cropRect = new Rect(0, 0, ActualWidth, ActualHeight);
        OverlayLayer.Children.Clear();
        OverlayLayer.Visibility = Visibility.Visible;
        RefreshCropLayer();
    }

    /// <summary>ESC：取消裁剪，恢复叠加层。返回是否已拦截（供全局 ESC 钩子）。</summary>
    public bool TryExitCropMode()
    {
        if (!_cropMode) return false;
        _cropMode = false;
        _cropDrag = CropDrag.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        OverlayLayer.Children.Clear();
        OverlayLayer.Visibility = Sticker.OcrWords is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        RefreshFrame();
        return true;
    }

    /// <summary>Enter / 双击：确认裁剪并应用。</summary>
    public bool ConfirmCrop()
    {
        if (!_cropMode) return false;
        var s = ToImageCoord(new Point(_cropRect.X, _cropRect.Y));
        var e = ToImageCoord(new Point(_cropRect.X + _cropRect.Width, _cropRect.Y + _cropRect.Height));
        var region = new Rect(s.X, s.Y, e.X - s.X, e.Y - s.Y);
        _cropMode = false;
        _cropDrag = CropDrag.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        OverlayLayer.Children.Clear();
        OverlayLayer.Visibility = Sticker.OcrWords is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        RefreshFrame();
        if (region.Width >= 4 && region.Height >= 4)
            _owner.ApplyImageOp(this, b => ImageProcessor.Crop(b, region));
        return true;
    }

    private void HandleCropDown(Point p)
    {
        _cropStart = p;
        _cropOrig = _cropRect;
        var r = _cropRect;
        const double tol = 8;
        // 角
        for (int i = 0; i < 4; i++)
        {
            var cpt = new Point(i is 0 or 2 ? r.X : r.X + r.Width, i < 2 ? r.Y : r.Y + r.Height);
            if (Dist(p, cpt) <= tol) { _cropDrag = CropDrag.Corner; _cropDragIndex = i; CaptureMouse(); return; }
        }
        // 边
        Point[] edges = { new(r.X + r.Width / 2, r.Y), new(r.X + r.Width, r.Y + r.Height / 2), new(r.X + r.Width / 2, r.Y + r.Height), new(r.X, r.Y + r.Height / 2) };
        for (int i = 0; i < 4; i++)
            if (Dist(p, edges[i]) <= tol) { _cropDrag = CropDrag.Edge; _cropDragIndex = i; CaptureMouse(); return; }
        // 框内移动
        if (r.Contains(p)) { _cropDrag = CropDrag.Move; CaptureMouse(); return; }
        _cropDrag = CropDrag.None;
    }

    private void HandleCropMove(Point p)
    {
        if (_cropDrag == CropDrag.None) { UpdateCropHover(p); return; }
        var r = _cropOrig;
        double x = r.X, y = r.Y, w = r.Width, h = r.Height;
        double right = x + w, bottom = y + h;
        switch (_cropDrag)
        {
            case CropDrag.Corner:
                switch (_cropDragIndex)
                {
                    case 0: x = Math.Min(p.X, right - 8); y = Math.Min(p.Y, bottom - 8); w = right - x; h = bottom - y; break;
                    case 1: y = Math.Min(p.Y, bottom - 8); w = Math.Max(p.X - x, 8); h = bottom - y; break;
                    case 2: x = Math.Min(p.X, right - 8); w = right - x; h = Math.Max(p.Y - y, 8); break;
                    default: w = Math.Max(p.X - x, 8); h = Math.Max(p.Y - y, 8); break;
                }
                break;
            case CropDrag.Edge:
                switch (_cropDragIndex)
                {
                    case 0: y = Math.Min(p.Y, bottom - 8); h = bottom - y; break;
                    case 1: w = Math.Max(p.X - x, 8); break;
                    case 2: h = Math.Max(p.Y - y, 8); break;
                    default: x = Math.Min(p.X, right - 8); w = right - x; break;
                }
                break;
            case CropDrag.Move:
                x = Math.Clamp(p.X - (_cropStart.X - r.X), 0, ActualWidth - w);
                y = Math.Clamp(p.Y - (_cropStart.Y - r.Y), 0, ActualHeight - h);
                break;
        }
        _cropRect = new Rect(x, y, w, h);
        RefreshCropLayer();
    }

    private void HandleCropUp(Point p)
    {
        bool wasDrag = _cropDrag != CropDrag.None;
        _cropDrag = CropDrag.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (wasDrag) { RefreshCropLayer(); return; }
        // 未拖动（单击）→ 双击判定
        var now = DateTime.Now;
        bool isDouble = (now - _lastCropUpTime).TotalMilliseconds < 500
                        && Math.Abs(p.X - _lastCropUpPos.X) < 6
                        && Math.Abs(p.Y - _lastCropUpPos.Y) < 6;
        _lastCropUpTime = now;
        _lastCropUpPos = p;
        if (isDouble) ConfirmCrop();
    }

    private DateTime _lastCropUpTime = DateTime.MinValue;
    private Point _lastCropUpPos;

    private void UpdateCropHover(Point p)
    {
        var r = _cropRect;
        const double tol = 8;
        for (int i = 0; i < 4; i++)
        {
            var cpt = new Point(i is 0 or 2 ? r.X : r.X + r.Width, i < 2 ? r.Y : r.Y + r.Height);
            if (Dist(p, cpt) <= tol) { Cursor = System.Windows.Input.Cursors.SizeNWSE; return; }
        }
        Point[] edges = { new(r.X + r.Width / 2, r.Y), new(r.X + r.Width, r.Y + r.Height / 2), new(r.X + r.Width / 2, r.Y + r.Height), new(r.X, r.Y + r.Height / 2) };
        for (int i = 0; i < 4; i++)
        {
            if (Dist(p, edges[i]) <= tol)
            {
                Cursor = (i == 0 || i == 2) ? System.Windows.Input.Cursors.SizeNS : System.Windows.Input.Cursors.SizeWE;
                return;
            }
        }
        Cursor = r.Contains(p) ? System.Windows.Input.Cursors.SizeAll : System.Windows.Input.Cursors.Cross;
    }

    private static double Dist(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>裁剪遮罩 + 裁剪框 + 手柄 + 尺寸 HUD。</summary>
    private void RefreshCropLayer()
    {
        OverlayLayer.Children.Clear();
        double w = ActualWidth, h = ActualHeight;
        var r = _cropRect;
        var mask = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00));
        // 上 / 下 / 左 / 右 四块遮罩
        AddMask(mask, 0, 0, w, Math.Max(0, r.Y));
        AddMask(mask, 0, r.Y + r.Height, w, Math.Max(0, h - r.Y - r.Height));
        AddMask(mask, 0, r.Y, Math.Max(0, r.X), r.Height);
        AddMask(mask, r.X + r.Width, r.Y, Math.Max(0, w - r.X - r.Width), r.Height);

        // 裁剪框
        var frame = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(1, r.Width), Height = Math.Max(1, r.Height),
            Stroke = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
            StrokeThickness = 2,
            Fill = Brushes.Transparent,
        };
        Canvas.SetLeft(frame, r.X);
        Canvas.SetTop(frame, r.Y);
        OverlayLayer.Children.Add(frame);

        // 手柄（角 + 边）
        var hb = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xC0));
        (double, double)[] pts =
        {
            (r.X, r.Y), (r.X + r.Width, r.Y), (r.X, r.Y + r.Height), (r.X + r.Width, r.Y + r.Height),
            (r.X + r.Width / 2, r.Y), (r.X + r.Width, r.Y + r.Height / 2), (r.X + r.Width / 2, r.Y + r.Height), (r.X, r.Y + r.Height / 2),
        };
        foreach (var (px, py) in pts)
        {
            var sq = new System.Windows.Shapes.Rectangle
            {
                Width = 8, Height = 8,
                Fill = Brushes.White,
                Stroke = hb,
                StrokeThickness = 1.5,
            };
            Canvas.SetLeft(sq, px - 4);
            Canvas.SetTop(sq, py - 4);
            OverlayLayer.Children.Add(sq);
        }

        // 尺寸 HUD（物理像素）
        var s = ToImageCoord(new Point(r.X, r.Y));
        var e = ToImageCoord(new Point(r.X + r.Width, r.Y + r.Height));
        var hud = new System.Windows.Controls.TextBlock
        {
            Text = $"{Math.Max(0, (int)(e.X - s.X))} × {Math.Max(0, (int)(e.Y - s.Y))}   Enter 确认 · Esc 取消 · 双击确认",
            FontSize = 12,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x00, 0x00)),
            Padding = new Thickness(6, 2, 6, 2),
        };
        Canvas.SetLeft(hud, Math.Clamp(r.X, 0, Math.Max(0, w - 300)));
        Canvas.SetTop(hud, Math.Clamp(r.Y - 24, 0, Math.Max(0, h - 22)));
        OverlayLayer.Children.Add(hud);
    }

    private void AddMask(SolidColorBrush brush, double x, double y, double w, double h)
    {
        if (w <= 0 || h <= 0) return;
        var rect = new System.Windows.Shapes.Rectangle { Width = w, Height = h, Fill = brush };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        OverlayLayer.Children.Add(rect);
    }

    /// <summary>是否处于标注模式（管理器钩子 / 菜单判断用）。</summary>
    public bool IsAnnotating => _annotating;

    /// <summary>是否处于裁剪模式（管理器钩子用）。</summary>
    public bool IsCropping => _cropMode;

    protected override void OnClosed(EventArgs e)
    {
        _hwndSource?.RemoveHook(_hook);
        _floating?.Close();   // 独立浮动条随贴图关闭（Owner 机制也会关，双保险）
        _annotBar?.Close();   // 标注工具条同理
        _owner.OnWindowClosed(this);
        base.OnClosed(e);
    }
}
