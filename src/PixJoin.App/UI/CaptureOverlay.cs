using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PixJoin.App.Imaging;
using PixJoin.App.Native;
using PixJoin.Core.Models;
using PixJoin.Core.Services;

namespace PixJoin.App.UI;

public enum CaptureAction { Pin, Copy, Save, Annotate, ScrollCapture }

public sealed record CaptureResult(BitmapSource Bitmap, Rect PhysicalRect, CaptureAction Action);

/// <summary>
/// 截图层：暗色遮罩 + 冻结画面 + 选区 + 像素级放大镜 + 尺寸 HUD + PixPin 风标注工具条。
/// 覆盖整个虚拟屏幕，单窗口处理跨显示器选区。
/// </summary>
public sealed class CaptureOverlay : PhysicalCanvasWindow
{
    private const int MagSource = 21;   // 放大镜取样边长（物理像素）
    private const int MagZoom = 6;      // 放大倍率

    private readonly ScreenShot _shot;
    private readonly Image _bgImage = new() { Stretch = Stretch.Fill };   // 冻结画面（底层）
    private readonly Path _mask = new() { Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)) };
    private readonly Rectangle _border = new() { Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xC0)), StrokeThickness = 1 };
    private readonly Canvas _selHandleLayer = new() { IsHitTestVisible = false };   // 选区拉伸手柄（四角+四边中点，常驻显示）
    private readonly Border _hud = new() { Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x18, 0x18, 0x18)), CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 3, 6, 3) };
    private readonly TextBlock _hudText = new() { Foreground = Brushes.White, FontFamily = new FontFamily("Consolas, Microsoft YaHei UI") };

    private readonly Border _magnifier = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x10, 0x10, 0x10)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xC0)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(2),
    };
    private readonly Image _magImage = new() { Stretch = Stretch.Fill };
    private readonly Canvas _magOverlay = new();
    private readonly TextBlock _magText = new()
    {
        Foreground = Brushes.White,
        FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
        Background = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)),
        Padding = new Thickness(4, 1, 4, 1),
    };

    private readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal };
    private readonly Border _toolbarHost = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x22, 0x22, 0x22)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(4),
    };

    private bool _selecting;
    private bool _hasSelection;
    private Point _startPhysical;
    private Point _currentPhysical;
    private Rect _selectionRect;   // 选区完成后冻结，防止后续鼠标移动污染

    // ---- 截图内标注（PixPin 风格：选区完成后直接在工具条选工具，在选区上画） ----
    private readonly List<Annotation> _annotations = new();
    private readonly Canvas _annotLayer = new() { IsHitTestVisible = false };
    private readonly Canvas _floatLayer = new() { IsHitTestVisible = true };   // 选中标注的浮动样式条（可点击）
    private AnnotationTool? _annotTool;
    private Color _annotColor = Color.FromRgb(0xE5, 0x39, 0x35);  // 默认红
    private double _annotThickness = 4;
    private bool _annotDashed;   // 当前线型：虚线/实线
    private ArrowStyle _annotArrowStyle = ArrowStyle.Solid;   // 当前箭头样式
    private bool _annotDrawing;
    private Point _annotStart;    // 相对选区左上角的物理像素
    private Point _annotLast;
    private readonly List<Point> _annotPenPts = new();
    private int _annotNumberSeq = 1;
    private double _scale = 1;    // 选区所在显示器 DPI 缩放（物理像素→DIP）

    // ---- 标注选中 / 编辑（画完保持选中，可拖动、改色、删除） ----
    private int? _selectedIndex;   // 当前选中的标注索引（null=无选中）
    private bool _draggingAnnot;   // 是否在拖动选中的标注
    private Point _dragOffset;     // 鼠标点与标注锚点的偏移（物理像素）

    // ---- 控制点系统（角点=对角缩放、边点=单边拉伸、内角点=圆角、箭头点=端点/弯曲） ----
    private AnnotHandle? _activeHandle;    // 当前拖动的控制点
    private Point? _arrowLivePt;           // 箭头中点控制点拖动中的实时位置（跟手显示）
    private double _arrowDragT = 0.5;      // 箭头中点控制点在曲线上锁定的参数 t（反解保证 B(t)=鼠标，控制点恒骑线）
    private bool _dragLive;                 // 拖动中（控制点/标注本体）：跳过调试文本与距离计算，保证跟手
    private bool _pickingColor;             // 取色模式：按 C 切换，点击复制当前像素色
    private bool _windowCapture;            // 窗口捕获模式：Tab 切换，悬停高亮，单击截取窗口

    // ---- 选区拉伸（截图画完选区后可拖动边缘/角落调整） ----
    private enum SelDrag { None, Corner, Edge }
    private SelDrag _selDrag = SelDrag.None;
    private int _selDragIndex;
    private bool _selDragging;
    private Rect _selOrigRect;
    private Rect? _winRect;                 // 当前高亮窗口物理矩形
    private string _winName = "";
    private readonly Rectangle _winHighlight = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
        StrokeThickness = 2,
        StrokeDashArray = new DoubleCollection { 4, 3 },
        Fill = new SolidColorBrush(Color.FromArgb(0x18, 0x3B, 0x82, 0xF6)),
        Visibility = Visibility.Collapsed,
    };
    private readonly Border _winNameText = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x3B, 0x82, 0xF6)),
        Padding = new Thickness(6, 2, 6, 2),
        CornerRadius = new CornerRadius(2),
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _winNameLabel = new() { FontSize = 12, Foreground = Brushes.White };

    // ---- [DEBUG] 拖动性能日志：内存队列，OnLeftUp 一次性写盘，避免日志 IO 干扰测量 ----
    private static readonly List<string> _dbgLog = new();
    private static readonly System.Diagnostics.Stopwatch _dbgSw = System.Diagnostics.Stopwatch.StartNew();
    private long _lastMoveTicks;
    private double _lastRefreshMs;
    private static void DbLog(string msg)
    {
        _dbgLog.Add($"{_dbgSw.ElapsedMilliseconds}ms|{msg}");
        if (_dbgLog.Count >= 200) DbFlush();   // 防内存无限增长，满 200 条先写一次
    }
    private static void DbFlush()
    {
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PixJoin");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllLines(System.IO.Path.Combine(dir, "annot_debug.log"), _dbgLog);
        }
        catch { }
        _dbgLog.Clear();
    }

    private static readonly Color[] AnnotColors =
    {
        Color.FromRgb(0xE5, 0x39, 0x35),  // 红
        Color.FromRgb(0xF5, 0x6C, 0x2C),  // 橙
        Color.FromRgb(0xF7, 0xC9, 0x48),  // 黄
        Color.FromRgb(0x34, 0xC7, 0x59),  // 绿
        Color.FromRgb(0x29, 0x9D, 0xF0),  // 蓝
        Color.FromRgb(0x9C, 0x27, 0xB0),  // 紫
        Color.FromRgb(0xF0, 0x62, 0x92),  // 粉
        Color.FromRgb(0x60, 0x70, 0x8B),  // 灰
    };

    public event Action<CaptureResult>? Completed;
    public event Action? Cancelled;

    public CaptureOverlay(ScreenShot shot) : base(clickThrough: false, noActivate: false)
    {
        _shot = shot;
        _bgImage.Source = shot.Bitmap;   // 冻结画面：选区洞显示的就是截图本身
        Cursor = Cursors.Cross;
        Focusable = true;

        BuildVisuals();
        PlaceOverVirtualScreen();

        MouseLeftButtonDown += OnLeftDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnLeftUp;
        MouseRightButtonDown += (_, _) => Cancel();
        KeyDown += OnKeyDown;
        MouseWheel += OnMouseWheel;

        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            RefreshCursorVisuals(GetCursorPhysical());
        };
    }

    private void BuildVisuals()
    {
        _hud.Child = _hudText;

        var magRoot = new Grid();
        magRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        magRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var magImageHost = new Border
        {
            Child = _magImage,
            Background = Brushes.Black,
            ClipToBounds = true,
        };
        RenderOptions.SetBitmapScalingMode(_magImage, BitmapScalingMode.NearestNeighbor);

        Grid.SetRow(magImageHost, 0);
        Grid.SetRow(_magOverlay, 0);
        Grid.SetRow(_magText, 1);

        var magPanel = new Grid();
        magPanel.Children.Add(magImageHost);
        magPanel.Children.Add(_magOverlay);

        magRoot.Children.Add(magPanel);
        magRoot.Children.Add(_magText);

        _magnifier.Child = magRoot;

        // 工具栏按物理像素布局，字号按所在显示器 DPI 缩放，保证视觉大小一致
        _toolbarHost.Child = _toolbar;
        _toolbarHost.Visibility = Visibility.Collapsed;

        _annotLayer.Visibility = Visibility.Collapsed;
        _floatLayer.Visibility = Visibility.Collapsed;

        Scene.Children.Add(_bgImage);          // 0 底层：冻结画面
        Scene.Children.Add(_mask);             // 1 暗色遮罩（选区挖洞露出冻结画面）
        Scene.Children.Add(_border);
        Scene.Children.Add(_selHandleLayer);   // 选区拉伸手柄
        Scene.Children.Add(_annotLayer);       // 标注层（只显示在选区内，坐标相对选区）
        Scene.Children.Add(_floatLayer);       // 浮动样式条（标注层之上，可点击）
        Scene.Children.Add(_hud);
        Scene.Children.Add(_magnifier);
        Scene.Children.Add(_toolbarHost);
        Scene.Children.Add(_winHighlight);
        _winNameText.Child = _winNameLabel;
        Scene.Children.Add(_winNameText);

        _pickChipBox = new Border
        {
            Width = 16, Height = 16,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        _pickChipText = new TextBlock { FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var chipStack = new StackPanel { Orientation = Orientation.Horizontal };
        chipStack.Children.Add(_pickChipBox);
        chipStack.Children.Add(_pickChipText);
        _pickChip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x00, 0x00, 0x00)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 3, 6, 3),
            Child = chipStack,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        Scene.Children.Add(_pickChip);

        HideAll();
    }

    /// <summary>窗口捕获：找光标下最顶层可见窗口，画高亮 + 标题。</summary>
    private void UpdateWindowCapture(Point cursor)
    {
        var hit = FindTopWindow(cursor);
        if (hit is null) { _winRect = null; _winHighlight.Visibility = Visibility.Collapsed; _winNameText.Visibility = Visibility.Collapsed; return; }

        var (hWnd, rect, title) = hit.Value;
        _winRect = rect;
        _winName = title;
        var local = ToLocalRect(rect);
        _winHighlight.Width = Math.Max(1, rect.Width);
        _winHighlight.Height = Math.Max(1, rect.Height);
        Canvas.SetLeft(_winHighlight, local.X);
        Canvas.SetTop(_winHighlight, local.Y);
        _winHighlight.Visibility = Visibility.Visible;

        _winNameLabel.Text = string.IsNullOrWhiteSpace(title) ? "（无标题窗口）" : title.Trim();
        _winNameText.Visibility = Visibility.Visible;
        Canvas.SetLeft(_winNameText, local.X);
        Canvas.SetTop(_winNameText, Math.Max(0, local.Y - 24));
        _ = hWnd;
    }

    /// <summary>光标下面积最小的可见顶层窗口（排除自身与系统工具窗口）。</summary>
    private (IntPtr hWnd, Rect rect, string title)? FindTopWindow(Point cursor)
    {
        IntPtr self = Handle;
        (IntPtr hWnd, Rect rect, string title)? best = null;
        double bestArea = double.MaxValue;
        Win32.EnumWindows((hWnd, _) =>
        {
            if (hWnd == self || hWnd == IntPtr.Zero) return true;
            if (!Win32.IsWindowVisible(hWnd)) return true;
            if (!Win32.GetWindowRect(hWnd, out var r)) return true;
            if (r.Left >= r.Right || r.Top >= r.Bottom) return true;
            if (cursor.X < r.Left || cursor.X >= r.Right || cursor.Y < r.Top || cursor.Y >= r.Bottom) return true;

            var cls = new System.Text.StringBuilder(256);
            Win32.GetClassName(hWnd, cls, 256);
            string cc = cls.ToString();
            if (cc is "Shell_TrayWnd" or "Progman" or "WorkerW" or "CiceroUIWndFrame"
                or "Windows.UI.Core.CoreWindow" or "SysShadow" or "ToolbarWindow32")
                return true;

            double area = (double)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area < bestArea)
            {
                var t = new System.Text.StringBuilder(512);
                Win32.GetWindowText(hWnd, t, 512);
                best = (hWnd, new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), t.ToString());
                bestArea = area;
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    private Border _pickChip;
    private Border _pickChipBox;
    private TextBlock _pickChipText;
    private string _lastPickedHex = "#FFFFFF";

    private void CopyPickedColor()
    {
        try
        {
            System.Windows.Clipboard.SetText(_lastPickedHex);
        }
        catch { }
    }

    /// <summary>以窗口矩形作为选区完成「贴图」。</summary>
    private CaptureAction PinWindow(Rect wr)
    {
        // 与选区完成一致：按物理矩形裁切
        _selectionRect = wr;
        _hasSelection = true;
        return CaptureAction.Pin;
    }

    private static Point GetCursorPhysical()
    {
        Win32.GetPhysicalCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    // ================= 鼠标 / 键盘事件 =================

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (_pickingColor)
        {
            CopyPickedColor();
            _pickingColor = false;
            RefreshCursorVisuals(GetCursorPhysical());
            e.Handled = true;
            return;
        }

        // 窗口捕获模式：单击 = 截取悬停高亮的窗口
        if (_windowCapture)
        {
            if (_winRect is { } wr)
            {
                Finish(PinWindow(wr));
                e.Handled = true;
                return;
            }
            e.Handled = true;
            return;
        }

        if (_hasSelection)
        {
            var p = GetCursorPhysical();
            var rel = new Point(p.X - _selectionRect.X, p.Y - _selectionRect.Y);

            // 0. 选区边缘/角落手柄：拉伸选区
            var sh = HitSelectionHandle(rel);
            if (sh != SelDrag.None)
            {
                _selDrag = sh;
                _selOrigRect = _selectionRect;
                _selDragging = true;
                CaptureMouse();
                e.Handled = true;
                return;
            }

            // 1. 有选中标注：先查控制点，再查本体拖动
            if (_selectedIndex is { } si)
            {
                var hh = AnnotationHandles.HitTest(AnnotationHandles.Get(_annotations[si], _arrowDragT), rel);
                if (hh is { } h)
                {
                    _activeHandle = h;
                    _dragLive = true;
                    CaptureMouse();
                    e.Handled = true;
                    return;
                }

                if (AnnotationHandles.HitShape(_annotations[si], rel, 5))
                {
                    _draggingAnnot = true;
                    _dragLive = true;
                    _dragOffset = new Point(rel.X - _annotations[si].X, rel.Y - _annotations[si].Y);
                    CaptureMouse();
                    e.Handled = true;
                    return;
                }
            }

            // 2. 点击在某个已固化标注上 → 选中它（不开始新绘制）
            var hit = HitTestAnnotation(rel);
            if (hit is { } hi)
            {
                _selectedIndex = hi;
                RefreshAnnotationLayer();
                ShowToolbar(_selectionRect);
                e.Handled = true;
                return;
            }

            // 3. 选了标注工具且点击在选区内空白处 → 取消选中，开始新绘制
            if (_annotTool.HasValue && _selectionRect.Contains(p))
            {
                _selectedIndex = null;
                StartAnnotation(p);
                e.Handled = true;
                return;
            }

            // 4. 点击在选区外 → 取消选中
            if (_selectedIndex.HasValue)
            {
                _selectedIndex = null;
                RefreshAnnotationLayer();
                ShowToolbar(_selectionRect);
            }
            return;   // 已选完（工具栏出现）时，交给工具栏处理
        }

        _selecting = true;
        _startPhysical = GetCursorPhysical();
        _currentPhysical = _startPhysical;
        CaptureMouse();
        UpdateSelection();
        RefreshCursorVisuals(_currentPhysical);
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_windowCapture)
        {
            UpdateWindowCapture(GetCursorPhysical());
            return;
        }

        if (_hasSelection)
        {
            if (_selDragging)
            {
                UpdateSelDrag(GetCursorPhysical());
                return;
            }

            // 悬停光标：控制点/标注本体给出对应提示标记
            UpdateHoverCursor(new Point(GetCursorPhysical().X - _selectionRect.X, GetCursorPhysical().Y - _selectionRect.Y));

            // 拖动控制点（缩放 / 拉伸 / 圆角 / 箭头调整）
            if (_activeHandle is { } ah && _selectedIndex is { } si2)
            {
                var p = GetCursorPhysical();
                var rel = new Point(p.X - _selectionRect.X, p.Y - _selectionRect.Y);
                long now = _dbgSw.ElapsedMilliseconds;
                if (_lastMoveTicks != 0 && now - _lastMoveTicks > 5)
                    DbLog($"MOVE {ah.Kind}:{ah.Index} gap={now - _lastMoveTicks}ms rel=({rel.X:0},{rel.Y:0})");
                _lastMoveTicks = now;
                var updated = AnnotationHandles.ApplyDrag(_annotations[si2], ah, rel, _arrowDragT);
                if (updated is not null)
                {
                    _annotations[si2] = updated;
                    _arrowLivePt = (ah.Kind == HandleKind.ArrowPt && ah.Index == 1) ? rel : (Point?)null;
                    RefreshAnnotationLayer();
                    DbLog($"  after-refresh {_lastRefreshMs:0.0}ms pts=({_annotations[si2].Points?[0]:0},{_annotations[si2].Points?[1]:0},{_annotations[si2].Points?[2]:0},{_annotations[si2].Points?[3]:0})");
                }
                return;
            }

            // 拖动选中的标注
            if (_draggingAnnot && _selectedIndex is { } si)
            {
                var p = GetCursorPhysical();
                var rel = new Point(p.X - _selectionRect.X, p.Y - _selectionRect.Y);
                var a = _annotations[si];
                // 拖动中不做任何钳制：保证 100% 跟手（钳制会截断移动量，是"不跟手"的根因）
                var delta = new Point(rel.X - _dragOffset.X - a.X, rel.Y - _dragOffset.Y - a.Y);
                if (Math.Abs(delta.X) > 0.01 || Math.Abs(delta.Y) > 0.01)
                {
                    long now = _dbgSw.ElapsedMilliseconds;
                    if (_lastMoveTicks != 0 && now - _lastMoveTicks > 5)
                        DbLog($"MOVE-ANNOT {a.Tool} gap={now - _lastMoveTicks}ms rel=({rel.X:0},{rel.Y:0}) delta=({delta.X:0},{delta.Y:0})");
                    _lastMoveTicks = now;
                    _annotations[si] = AnnotationHandles.Move(a, delta);
                    RefreshAnnotationLayer();
                    DbLog($"  after-refresh {_lastRefreshMs:0.0}ms");
                }
                return;
            }

            if (_annotDrawing)
            {
                var p = GetCursorPhysical();
                _annotLast = new Point(p.X - _selectionRect.X, p.Y - _selectionRect.Y);
                if (_annotTool == AnnotationTool.Pen) _annotPenPts.Add(_annotLast);
                RefreshAnnotationLayer();
            }
            return;
        }

        _currentPhysical = GetCursorPhysical();
        if (_selecting) UpdateSelection();
        // 无论是否在拖选都要刷新放大镜，保证它始终跟随光标
        RefreshCursorVisuals(_currentPhysical);
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_selDragging)
        {
            _selDragging = false;
            _selDrag = SelDrag.None;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (_activeHandle is { })
        {
            _activeHandle = null;
            _dragLive = false;
            _arrowLivePt = null;
            DbLog($"LEFTUP handle end (lastRefresh={_lastRefreshMs:0.0}ms)");
            DbFlush();
            ReleaseMouseCapture();
            RefreshAnnotationLayer();   // 恢复控制点显示（拖动中曾隐藏）
            e.Handled = true;
            return;
        }

        if (_draggingAnnot)
        {
            _draggingAnnot = false;
            _dragLive = false;
            DbLog($"LEFTUP move end (lastRefresh={_lastRefreshMs:0.0}ms)");
            DbFlush();
            // 防丢：若标注完全在选区外（不可见），拉回选区边缘；部分可见/完全包裹选区则不动
            if (_selectedIndex is { } sidx && sidx >= 0 && sidx < _annotations.Count)
            {
                var nb = GetAnnotBounds(_annotations[sidx]);
                double pad = Math.Max(3, _annotations[sidx].Thickness * 1.5);
                var sel = new Rect(pad, pad, Math.Max(1, _selectionRect.Width - pad * 2), Math.Max(1, _selectionRect.Height - pad * 2));
                if (!nb.IntersectsWith(sel))
                {
                    double dx = 0, dy = 0;
                    if (nb.Right < sel.Left) dx = sel.Left - nb.Right;
                    else if (nb.Left > sel.Right) dx = sel.Right - nb.Left;
                    if (nb.Bottom < sel.Top) dy = sel.Top - nb.Bottom;
                    else if (nb.Top > sel.Bottom) dy = sel.Bottom - nb.Top;
                    if (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01)
                    {
                        _annotations[sidx] = AnnotationHandles.Move(_annotations[sidx], new Point(dx, dy));
                        DbLog($"LEFTUP pullback ({dx:0},{dy:0})");
                        RefreshAnnotationLayer();
                    }
                }
            }
            ReleaseMouseCapture();
            RefreshAnnotationLayer();   // 恢复控制点显示（拖动中曾隐藏；含拉回时以最终位置重绘）
            e.Handled = true;
            return;
        }

        if (_annotDrawing)
        {
            _annotDrawing = false;
            ReleaseMouseCapture();
            FinishAnnotation();
            e.Handled = true;
            return;
        }

        if (!_selecting) return;
        _selecting = false;
        ReleaseMouseCapture();

        var rect = CurrentRect();
        if (rect.Width < 3 || rect.Height < 3)
        {
            ResetSelection();
            return;
        }

        _hasSelection = true;
        _selectionRect = rect;   // 冻结选区：此后鼠标移到按钮/工具栏不会改变最终截图范围
        _scale = MonitorHelper.ScaleAtPhysicalPoint(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
        ShowToolbar(rect);
        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_hasSelection && _selectedIndex is { } si)
        {
            // 滚轮只调整选中标注的粗细（±1，clamp 1~20）
            double delta = e.Delta > 0 ? 1 : -1;
            var a = _annotations[si];
            double nt = Math.Clamp(a.Thickness + delta, 1, 20);
            if (Math.Abs(nt - a.Thickness) > 0.01)
            {
                _annotations[si] = WithColorThickness(a, thickness: nt);
                RefreshAnnotationLayer();
            }
            e.Handled = true;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            // 无选区时切换窗口捕获模式；有选区时进入后单击直接截取该窗口
            _windowCapture = !_windowCapture;
            _winRect = null;
            _winHighlight.Visibility = Visibility.Collapsed;
            Cursor = _windowCapture ? Cursors.Hand : Cursors.Cross;
            if (_windowCapture) UpdateWindowCapture(GetCursorPhysical());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_windowCapture)
            {
                _windowCapture = false;
                _winRect = null;
                _winHighlight.Visibility = Visibility.Collapsed;
                Cursor = Cursors.Cross;
                e.Handled = true;
                return;
            }

            // 标注模式下 ESC 优先退出标注工具选择（再次 ESC 才逐级取消）
            if (_hasSelection && _annotTool.HasValue)
            {
                _annotTool = null;
                RefreshAnnotationLayer();
                ShowToolbar(_selectionRect);
            }
            else if (_hasSelection && _selectedIndex.HasValue)
            {
                _selectedIndex = null;
                RefreshAnnotationLayer();
                ShowToolbar(_selectionRect);
            }
            else if (_hasSelection) ResetSelection();
            else Cancel();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _hasSelection && !_annotDrawing)
        {
            Finish(CaptureAction.Pin);
            e.Handled = true;
        }
        else if (e.Key == Key.C && _hasSelection && !_annotDrawing)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                Finish(CaptureAction.Copy);   // Ctrl+C = 复制截图
            }
            else
            {
                _pickingColor = !_pickingColor;   // C = 取色（再按关闭）
                RefreshCursorVisuals(GetCursorPhysical());
            }
            e.Handled = true;
        }
        else if (e.Key == Key.S && _hasSelection && !_annotDrawing)
        {
            Finish(CaptureAction.Save);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _hasSelection && _selectedIndex is { } dsi)
        {
            _annotations.RemoveAt(dsi);
            _selectedIndex = null;
            RefreshAnnotationLayer();
            ShowToolbar(_selectionRect);
            e.Handled = true;
        }
        else if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && _hasSelection)
        {
            if (_annotations.Count > 0)
            {
                _annotations.RemoveAt(_annotations.Count - 1);
                if (_selectedIndex >= _annotations.Count) _selectedIndex = null;
                RefreshAnnotationLayer();
                ShowToolbar(_selectionRect);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.A && _hasSelection && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            Finish(CaptureAction.Annotate);   // 截完直接进入贴图标注模式
            e.Handled = true;
        }
    }

    // ================= 选区逻辑（保留 v0.2） =================

    /// <summary>根据悬停位置设置光标：控制点=对应拉伸方向，标注本体=四向移动，否则恢复。</summary>
    private void UpdateHoverCursor(Point rel)
    {
        // 选区边缘/角落：拉伸光标（优先于标注）
        var sh = HitSelectionHandle(rel);
        if (sh != SelDrag.None)
        {
            Cursor = sh switch
            {
                SelDrag.Corner => (_selDragIndex is 0 or 3) ? Cursors.SizeNWSE : Cursors.SizeNESW,
                _ => (_selDragIndex is 0 or 2) ? Cursors.SizeNS : Cursors.SizeWE,
            };
            return;
        }

        if (_selectedIndex is { } si && si >= 0 && si < _annotations.Count)
        {
            var hh = AnnotationHandles.HitTest(AnnotationHandles.Get(_annotations[si], _arrowDragT), rel);
            if (hh is { } h)
            {
                Cursor = h.Kind switch
                {
                    HandleKind.Edge => (h.Index == 0 || h.Index == 2) ? Cursors.SizeNS : Cursors.SizeWE,
                    HandleKind.Corner => (h.Index == 0 || h.Index == 3) ? Cursors.SizeNWSE : Cursors.SizeNESW,
                    HandleKind.Round => Cursors.SizeNWSE,
                    HandleKind.ArrowPt => h.Index == 1 ? Cursors.Hand : Cursors.SizeAll,
                    HandleKind.Move => Cursors.SizeAll,
                    _ => Cursors.Arrow,
                };
                return;
            }
            var b = GetAnnotBounds(_annotations[si]);
            b.Inflate(5, 5);
            if (b.Contains(rel)) { Cursor = Cursors.SizeAll; return; }
        }
        Cursor = _annotTool.HasValue ? Cursors.Cross : Cursors.Arrow;
    }

    /// <summary>命中选区拉伸手柄（rel 为相对选区左上角的物理坐标）。</summary>
    private SelDrag HitSelectionHandle(Point rel)
    {
        double w = _selectionRect.Width, h = _selectionRect.Height;
        const double tol = 7;
        (double, double)[] corners = { (0, 0), (w, 0), (0, h), (w, h) };
        for (int i = 0; i < 4; i++)
            if (Dist(rel, new Point(corners[i].Item1, corners[i].Item2)) <= tol) { _selDragIndex = i; return SelDrag.Corner; }
        (double, double)[] edges = { (w / 2, 0), (w, h / 2), (w / 2, h), (0, h / 2) };
        for (int i = 0; i < 4; i++)
            if (Dist(rel, new Point(edges[i].Item1, edges[i].Item2)) <= tol) { _selDragIndex = i; return SelDrag.Edge; }
        return SelDrag.None;
    }

    private static double Dist(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>按手柄拖动更新选区（锚定对角/对边，最小 6px）。</summary>
    private void UpdateSelDrag(Point p)
    {
        var r = _selOrigRect;
        double x = r.X, y = r.Y, right = x + r.Width, bottom = y + r.Height;
        const double min = 6;
        switch (_selDrag)
        {
            case SelDrag.Corner:
                switch (_selDragIndex)
                {
                    case 0: x = Math.Min(p.X, right - min); y = Math.Min(p.Y, bottom - min); break;
                    case 1: y = Math.Min(p.Y, bottom - min); right = Math.Max(p.X, x + min); break;
                    case 2: x = Math.Min(p.X, right - min); bottom = Math.Max(p.Y, y + min); break;
                    default: right = Math.Max(p.X, x + min); bottom = Math.Max(p.Y, y + min); break;
                }
                break;
            case SelDrag.Edge:
                switch (_selDragIndex)
                {
                    case 0: y = Math.Min(p.Y, bottom - min); break;
                    case 1: right = Math.Max(p.X, x + min); break;
                    case 2: bottom = Math.Max(p.Y, y + min); break;
                    default: x = Math.Min(p.X, right - min); break;
                }
                break;
        }
        _selectionRect = new Rect(x, y, right - x, bottom - y);
        UpdateSelection();
        MoveToolbar(_selectionRect);
        RefreshAnnotationLayer();   // 标注相对坐标不变 → 视觉随选区移动
    }

    private Rect CurrentRect()
    {
        if (_hasSelection) return _selectionRect;
        double x = Math.Min(_startPhysical.X, _currentPhysical.X);
        double y = Math.Min(_startPhysical.Y, _currentPhysical.Y);
        double w = Math.Abs(_currentPhysical.X - _startPhysical.X);
        double h = Math.Abs(_currentPhysical.Y - _startPhysical.Y);
        return new Rect(x, y, w, h);
    }

    private void UpdateSelection()
    {
        var r = CurrentRect();

        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        var vs = MonitorHelper.VirtualScreen;
        group.Children.Add(new RectangleGeometry(new Rect(0, 0, vs.Width, vs.Height)));
        group.Children.Add(new RectangleGeometry(ToLocalRect(r)));
        _mask.Data = group;
        _mask.Visibility = Visibility.Visible;

        var local = ToLocalRect(r);
        Canvas.SetLeft(_border, local.Left);
        Canvas.SetTop(_border, local.Top);
        _border.Width = Math.Max(0, local.Width - 1);
        _border.Height = Math.Max(0, local.Height - 1);
        _border.Visibility = Visibility.Visible;

        double scale = MonitorHelper.ScaleAtPhysicalPoint(r.Left + r.Width / 2, r.Top + r.Height / 2);
        _hudText.FontSize = 12 * scale;
        _hudText.Text = $"{(int)Math.Round(r.Width)} × {(int)Math.Round(r.Height)}";
        _hud.Visibility = Visibility.Visible;

        double hudX = local.Left;
        double hudY = local.Top - 26 * scale;
        if (hudY < 0) hudY = local.Top + 4;
        Canvas.SetLeft(_hud, hudX);
        Canvas.SetTop(_hud, hudY);

        UpdateSelHandles(local);
    }

    /// <summary>选区拉伸手柄：四角 8px + 四边中点 6px，中心对齐角点/边中点，与 HitSelectionHandle 命中范围一致。</summary>
    private void UpdateSelHandles(Rect local)
    {
        _selHandleLayer.Children.Clear();
        if (!_hasSelection)
        {
            _selHandleLayer.Visibility = Visibility.Collapsed;
            return;
        }
        const double cs = 8, es = 6;
        (double X, double Y, double S)[] pts =
        {
            (local.Left, local.Top, cs), (local.Right, local.Top, cs),
            (local.Left, local.Bottom, cs), (local.Right, local.Bottom, cs),
            (local.Left + local.Width / 2, local.Top, es), (local.Right, local.Top + local.Height / 2, es),
            (local.Left + local.Width / 2, local.Bottom, es), (local.Left, local.Top + local.Height / 2, es),
        };
        var stroke = new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5));
        foreach (var (x, y, s) in pts)
        {
            var r = new Rectangle
            {
                Width = s, Height = s,
                Fill = Brushes.White,
                Stroke = stroke,
                StrokeThickness = 1.2,
            };
            Canvas.SetLeft(r, x - s / 2);
            Canvas.SetTop(r, y - s / 2);
            _selHandleLayer.Children.Add(r);
        }
        _selHandleLayer.Visibility = Visibility.Visible;
    }

    private Rect ToLocalRect(Rect physical)
    {
        var vs = MonitorHelper.VirtualScreen;
        return new Rect(physical.Left - vs.Left, physical.Top - vs.Top, physical.Width, physical.Height);
    }

    private void RefreshCursorVisuals(Point cursor)
    {
        if (_hasSelection)
        {
            _magnifier.Visibility = Visibility.Collapsed;
            if (_pickingColor) { UpdatePickChip(cursor); _pickChip.Visibility = Visibility.Visible; }
            else _pickChip.Visibility = Visibility.Collapsed;
            return;
        }

        int pcx = (int)Math.Floor(cursor.X);
        int pcy = (int)Math.Floor(cursor.Y);
        var src = new Rect(pcx - MagSource / 2, pcy - MagSource / 2, MagSource, MagSource);
        var crop = _shot.Crop(src);

        if (crop is not null)
        {
            _magImage.Source = crop;
            _magImage.Width = crop.PixelWidth * MagZoom;
            _magImage.Height = crop.PixelHeight * MagZoom;
        }

        _magOverlay.Children.Clear();
        if (crop is not null)
        {
            int cw = crop.PixelWidth;
            int ch = crop.PixelHeight;
            double viewW = cw * MagZoom;
            double viewH = ch * MagZoom;
            double cx = viewW / 2.0;
            double cy = viewH / 2.0;

            _magOverlay.Width = viewW;
            _magOverlay.Height = viewH;

            // 1) 像素网格线
            var gridBrush = new SolidColorBrush(Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF));
            for (int i = 1; i < cw; i++)
            {
                double x = i * MagZoom;
                _magOverlay.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = viewH, Stroke = gridBrush, StrokeThickness = 1 });
            }
            for (int i = 1; i < ch; i++)
            {
                double y = i * MagZoom;
                _magOverlay.Children.Add(new Line { X1 = 0, X2 = viewW, Y1 = y, Y2 = y, Stroke = gridBrush, StrokeThickness = 1 });
            }

            // 2) 中心交界线：十字准星画在中心像素的四条边界上
            double half = MagZoom / 2.0;
            var crossBrush = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));
            _magOverlay.Children.Add(new Line { X1 = cx - half, X2 = cx - half, Y1 = 0, Y2 = viewH, Stroke = crossBrush, StrokeThickness = 1 });
            _magOverlay.Children.Add(new Line { X1 = cx + half, X2 = cx + half, Y1 = 0, Y2 = viewH, Stroke = crossBrush, StrokeThickness = 1 });
            _magOverlay.Children.Add(new Line { X1 = 0, X2 = viewW, Y1 = cy - half, Y2 = cy - half, Stroke = crossBrush, StrokeThickness = 1 });
            _magOverlay.Children.Add(new Line { X1 = 0, X2 = viewW, Y1 = cy + half, Y2 = cy + half, Stroke = crossBrush, StrokeThickness = 1 });

            // 3) 中心像素高亮
            var center = new Rectangle
            {
                Width = MagZoom, Height = MagZoom,
                Fill = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xE0, 0x66)),
                Stroke = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xE0, 0x66)),
                StrokeThickness = 1,
            };
            Canvas.SetLeft(center, cx - half);
            Canvas.SetTop(center, cy - half);
            _magOverlay.Children.Add(center);
        }

        double scale = MonitorHelper.ScaleAtPhysicalPoint(cursor.X, cursor.Y);
        _magText.FontSize = 11 * scale;

        var px = _shot.Crop(new Rect(cursor.X, cursor.Y, 1, 1));
        if (px is not null)
        {
            var buf = new byte[4];
            var conv = px.Format == PixelFormats.Bgra32 || px.Format == PixelFormats.Pbgra32
                ? px
                : new FormatConvertedBitmap(px, PixelFormats.Bgra32, null, 0);
            conv.CopyPixels(buf, 4, 0);
            string hex = $"#{buf[2]:X2}{buf[1]:X2}{buf[0]:X2}";
            if (_pickingColor) _magText.Text = $"{hex}  点击复制";
            else _magText.Text = $"{hex}  ({cursor.X}, {cursor.Y})";
            _lastPickedHex = hex;
        }

        double mw = _magnifier.ActualWidth > 0 ? _magnifier.ActualWidth : MagSource * MagZoom + 40;
        double mh = _magnifier.ActualHeight > 0 ? _magnifier.ActualHeight : MagSource * MagZoom + 40;

        if (_pickingColor) { UpdatePickChip(cursor); _pickChip.Visibility = Visibility.Visible; }
        else _pickChip.Visibility = Visibility.Collapsed;

        var pos = ComputeMagnifierPos(cursor, mw, mh, CurrentSelectionRect());
        var local = ToLocal(pos);
        Canvas.SetLeft(_magnifier, local.X);
        Canvas.SetTop(_magnifier, local.Y);
        _magnifier.Visibility = Visibility.Visible;
    }

    /// <summary>取色色卡：跟随光标，实时显示像素色 + HEX/RGB + 点击复制提示。</summary>
    private void UpdatePickChip(Point cursor)
    {
        var px = _shot.Crop(new Rect(cursor.X, cursor.Y, 1, 1));
        if (px is null) return;
        var buf = new byte[4];
        var conv = px.Format == PixelFormats.Bgra32 || px.Format == PixelFormats.Pbgra32
            ? px
            : new FormatConvertedBitmap(px, PixelFormats.Bgra32, null, 0);
        conv.CopyPixels(buf, 4, 0);
        _pickChipBox.Background = new SolidColorBrush(Color.FromRgb(buf[2], buf[1], buf[0]));
        _pickChipText.Text = $"#{buf[2]:X2}{buf[1]:X2}{buf[0]:X2}  {buf[2]:D3},{buf[1]:D3},{buf[0]:D3}  点击复制";
        _lastPickedHex = $"#{buf[2]:X2}{buf[1]:X2}{buf[0]:X2}";
        var local = ToLocal(cursor);
        Canvas.SetLeft(_pickChip, Math.Min(local.X + 20, MonitorHelper.VirtualScreen.Width - 260));
        Canvas.SetTop(_pickChip, local.Y + 20);
    }

    private Rect? CurrentSelectionRect()
    {
        if (!_selecting) return null;
        double x = Math.Min(_startPhysical.X, _currentPhysical.X);
        double y = Math.Min(_startPhysical.Y, _currentPhysical.Y);
        double w = Math.Abs(_currentPhysical.X - _startPhysical.X);
        double h = Math.Abs(_currentPhysical.Y - _startPhysical.Y);
        if (w < 1 && h < 1) return null;
        return new Rect(x, y, w, h);
    }

    private Point ComputeMagnifierPos(Point cursor, double mw, double mh, Rect? sel)
    {
        var vs = MonitorHelper.VirtualScreen;
        const double gap = 10;

        if (sel is null)
        {
            double bx = Math.Max(vs.Left, Math.Min(cursor.X + 24, vs.Right - mw));
            double by = Math.Max(vs.Top, Math.Min(cursor.Y + 24, vs.Bottom - mh));
            return new Point(bx, by);
        }

        var s = sel.Value;
        double ClampX(double x) => Math.Max(vs.Left, Math.Min(x, vs.Right - mw));
        double ClampY(double y) => Math.Max(vs.Top, Math.Min(y, vs.Bottom - mh));

        var candidates = new[]
        {
            (s.Right + gap,        ClampY(cursor.Y - mh / 2)),
            (ClampX(cursor.X - mw / 2), s.Bottom + gap),
            (s.Left - gap - mw,    ClampY(cursor.Y - mh / 2)),
            (ClampX(cursor.X - mw / 2), s.Top - gap - mh),
        };

        Point? best = null;
        double bestDist = double.MaxValue;
        foreach (var (lx, ly) in candidates)
        {
            bool inside = lx >= vs.Left && ly >= vs.Top && lx + mw <= vs.Right && ly + mh <= vs.Bottom;
            if (!inside) continue;
            if (new Rect(lx, ly, mw, mh).IntersectsWith(s)) continue;
            double dx = (lx + mw / 2) - cursor.X;
            double dy = (ly + mh / 2) - cursor.Y;
            double d = dx * dx + dy * dy;
            if (d < bestDist) { bestDist = d; best = new Point(lx, ly); }
        }
        if (best is { } b) return b;

        double fx = Math.Max(vs.Left, Math.Min(cursor.X + 24, vs.Right - mw));
        double fy = Math.Max(vs.Top, Math.Min(cursor.Y + 24, vs.Bottom - mh));
        return new Point(fx, fy);
    }

    // ================= 标注工具条 =================

    /// <summary>工具符号（PixPin 风格紧凑图标 + 下拉箭头）。</summary>
    private static readonly (AnnotationTool Tool, string Glyph, string Tip)[] AnnotToolDefs =
    {
        (AnnotationTool.Rect, "▭", "矩形"),
        (AnnotationTool.Ellipse, "◯", "椭圆"),
        (AnnotationTool.Arrow, "➔", "箭头"),
        (AnnotationTool.Pen, "✎", "画笔"),
        (AnnotationTool.Text, "T", "文字"),
        (AnnotationTool.Highlight, "🖍", "高亮"),
        (AnnotationTool.Mosaic, "▦", "马赛克"),
        (AnnotationTool.Number, "①", "序号"),
    };

    private void ShowToolbar(Rect physicalRect)
    {
        _magnifier.Visibility = Visibility.Collapsed;
        double scale = MonitorHelper.ScaleAtPhysicalPoint(physicalRect.Left + physicalRect.Width / 2,
                                                          physicalRect.Top + physicalRect.Height / 2);

        _toolbar.Children.Clear();

        // 撤销
        var undo = MakeToolButton("↶", "撤销标注 (Ctrl+Z)", null, scale, active: false);
        undo.Click += (_, _) =>
        {
            if (_annotations.Count > 0)
            {
                _annotations.RemoveAt(_annotations.Count - 1);
                if (_selectedIndex >= _annotations.Count) _selectedIndex = null;
                RefreshAnnotationLayer();
                ShowToolbar(_selectionRect);
            }
        };
        _toolbar.Children.Add(undo);

        // 标注工具区（8 工具 + 颜色 + 粗细 + 样式下拉）：共享组件，与贴图工具条同一份代码
        var tools = new AnnotationToolsControl(_annotTool, _annotColor, _annotThickness, _annotArrowStyle, _annotDashed, scale);
        tools.ToolChanged += t =>
        {
            _annotTool = t;
            RefreshAnnotationLayer();
            ShowToolbar(_selectionRect);
        };
        tools.ColorChanged += c =>
        {
            _annotColor = c;
            if (_selectedIndex is { } si2 && si2 < _annotations.Count)
                _annotations[si2] = WithColorThickness(_annotations[si2], color: c);
            RefreshAnnotationLayer();
            ShowToolbar(_selectionRect);
        };
        tools.ThicknessChanged += t2 =>
        {
            _annotThickness = t2;
            if (_selectedIndex is { } si2 && si2 < _annotations.Count)
                _annotations[si2] = WithColorThickness(_annotations[si2], thickness: t2);
            RefreshAnnotationLayer();
            ShowToolbar(_selectionRect);
        };
        tools.StyleApplied += (s, d) => OnArrowStyleApply(s, d);
        _toolbar.Children.Add(tools);

        _toolbar.Children.Add(MakeSeparator(scale));
        _toolbar.Children.Add(MakeActionButton("长截图", CaptureAction.ScrollCapture, scale));
        _toolbar.Children.Add(MakeActionButton("贴图", CaptureAction.Pin, scale, primary: true));
        _toolbar.Children.Add(MakeActionButton("复制", CaptureAction.Copy, scale));
        _toolbar.Children.Add(MakeActionButton("保存", CaptureAction.Save, scale));
        _toolbar.Children.Add(MakeActionButton("标注", CaptureAction.Annotate, scale));
        _toolbar.Children.Add(MakeCloseButton(scale));

        _toolbarHost.Visibility = Visibility.Visible;
        _toolbarHost.UpdateLayout();
        MoveToolbar(physicalRect);
    }

    /// <summary>仅移动工具栏位置（不重建按钮），供选区拉伸等高频拖动场景。</summary>
    private void MoveToolbar(Rect physicalRect)
    {
        double scale = MonitorHelper.ScaleAtPhysicalPoint(
            physicalRect.Left + physicalRect.Width / 2, physicalRect.Top + physicalRect.Height / 2);
        double bw = _toolbarHost.ActualWidth > 0 ? _toolbarHost.ActualWidth : 420;
        double bh = _toolbarHost.ActualHeight > 0 ? _toolbarHost.ActualHeight : 36;

        var local = ToLocalRect(physicalRect);
        double lx = local.Left + physicalRect.Width - bw;
        double ly = local.Bottom + 8 * scale;
        if (ly + bh > MonitorHelper.VirtualScreen.Height) ly = local.Bottom - bh - 8 * scale;
        if (lx < 0) lx = 0;

        Canvas.SetLeft(_toolbarHost, lx);
        Canvas.SetTop(_toolbarHost, ly);
    }

    private static Border MakeSeparator(double scale) => new()
    {
        Width = 1, Height = 18 * scale,
        Background = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
        Margin = new Thickness(5, 0, 5, 0),
    };

    /// <summary>工具按钮：主体 + 右下角 ▾（点击弹 menuBuilder 生成的菜单）。</summary>
    private Button MakeToolButton(string glyph, string tip, ContextMenu? menu, double scale, bool active)
    {
        var body = new Border
        {
            Child = new TextBlock
            {
                Text = glyph,
                Foreground = active ? Brushes.Black : Brushes.White,
                FontSize = 14 * scale,
                FontFamily = new FontFamily("Segoe UI Symbol, Microsoft YaHei UI"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Background = active
                ? new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xC0))
                : new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            Width = 26 * scale, Height = 26 * scale,
            CornerRadius = new CornerRadius(3 * scale),
        };

        Grid g;
        if (menu is not null)
        {
            // 图标 + 右侧并排的小三角下拉箭头（不覆盖图标）
            var caret = new TextBlock
            {
                Text = "▾",
                Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
                FontSize = 8 * scale,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1 * scale, 0, 0, 0),
            };
            g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(body, 0);
            Grid.SetColumn(caret, 1);
            g.Children.Add(body);
            g.Children.Add(caret);
        }
        else
        {
            g = new Grid();
            g.Children.Add(body);
        }

        var b = new Button
        {
            Content = g,
            Margin = new Thickness(2 * scale, 0, 2 * scale, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Cursor = Cursors.Arrow,
            ToolTip = tip,
        };
        if (menu is not null)
        {
            b.Click += (_, _) => menu.PlacementTarget = b;
            b.ContextMenu = menu;
            // 右键/箭头下拉：左键直接弹菜单（工具切换用单独处理），点 ▾ 弹菜单
            var caret = g.Children[g.Children.Count - 1];
            if (caret is TextBlock tb)
            {
                tb.MouseLeftButtonDown += (_, _) =>
                {
                    menu.PlacementTarget = b;
                    menu.IsOpen = true;
                };
            }
        }
        return b;
    }

    /// <summary>线型下拉菜单（实线 / 虚线）。</summary>
    private void OnLineStyleApply(bool dashed)
    {
        _annotDashed = dashed;
        ApplyDashedToSelected();
        ShowToolbar(_selectionRect);
    }

    private void OnArrowStyleApply(ArrowStyle style, bool dashed)
    {
        _annotArrowStyle = style;
        _annotDashed = dashed;
        ApplyArrowStyleToSelected();
        ApplyDashedToSelected();
        ShowToolbar(_selectionRect);
    }

    private void ApplyArrowStyleToSelected()
    {
        if (_selectedIndex is not { } si || si < 0 || si >= _annotations.Count) return;
        var a = _annotations[si];
        if (a.Tool != AnnotationTool.Arrow) return;
        _annotations[si] = new Annotation
        {
            Tool = a.Tool, X = a.X, Y = a.Y, W = a.W, H = a.H,
            Points = a.Points, Text = a.Text, Number = a.Number,
            Color = a.Color, Thickness = a.Thickness, Dashed = a.Dashed, Arrow = _annotArrowStyle,
            CornerRadius = a.CornerRadius,
        };
        RefreshAnnotationLayer();
    }

    private void ApplyDashedToSelected()
    {
        if (_selectedIndex is not { } si || si < 0 || si >= _annotations.Count) return;
        var a = _annotations[si];
        _annotations[si] = new Annotation
        {
            Tool = a.Tool, X = a.X, Y = a.Y, W = a.W, H = a.H,
            Points = a.Points, Text = a.Text, Number = a.Number,
            Color = a.Color, Thickness = a.Thickness, Dashed = _annotDashed, Arrow = a.Arrow,
            CornerRadius = a.CornerRadius,
        };
        RefreshAnnotationLayer();
    }

    private Button MakeActionButton(string text, CaptureAction action, double scale, bool primary = false)
    {
        var b = new Button
        {
            Content = text,
            Margin = new Thickness(3, 0, 3, 0),
            Padding = new Thickness(8 * scale, 5 * scale, 8 * scale, 5 * scale),
            FontSize = 12 * scale,
            Foreground = Brushes.White,
            Background = primary
                ? new SolidColorBrush(Color.FromRgb(0x00, 0x8C, 0x7A))
                : new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Arrow,
        };
        b.Click += (_, _) => Finish(action);
        return b;
    }

    private Button MakeCloseButton(double scale)
    {
        var b = new Button
        {
            Content = "✕",
            Margin = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(8 * scale, 5 * scale, 8 * scale, 5 * scale),
            FontSize = 12 * scale,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(0x5A, 0x2A, 0x2A)),
            BorderThickness = new Thickness(0),
            ToolTip = "取消 (Esc)",
            Cursor = Cursors.Arrow,
        };
        b.Click += (_, _) => Cancel();
        return b;
    }

    // ================= 标注绘制 =================

    private void StartAnnotation(Point physical)
    {
        _annotDrawing = true;
        _annotStart = new Point(physical.X - _selectionRect.X, physical.Y - _selectionRect.Y);
        _annotLast = _annotStart;
        _annotPenPts.Clear();
        _annotPenPts.Add(_annotStart);

        if (_annotTool == AnnotationTool.Text)
        {
            _annotDrawing = false;
            var input = new TextInputWindow();
            input.Owner = this;
            input.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            if (input.ShowDialog() == true)
            {
                _annotations.Add(new Annotation
                {
                    Tool = AnnotationTool.Text,
                    X = _annotStart.X, Y = _annotStart.Y,
                    Text = input.ResultText,
                    Color = _annotColor, Thickness = _annotThickness, Dashed = _annotDashed,
                });
                _selectedIndex = _annotations.Count - 1;   // 先选中再刷新 → 画完立即显示控制点
                RefreshAnnotationLayer();
                ShowToolbar(_selectionRect);
            }
            return;
        }

        if (_annotTool == AnnotationTool.Number)
        {
            _annotDrawing = false;
            _annotations.Add(new Annotation
            {
                Tool = AnnotationTool.Number,
                X = _annotStart.X, Y = _annotStart.Y,
                Number = _annotNumberSeq++,
                Color = _annotColor, Thickness = _annotThickness, Dashed = _annotDashed,
            });
            _selectedIndex = _annotations.Count - 1;   // 先选中再刷新 → 画完立即显示控制点
            RefreshAnnotationLayer();
            ShowToolbar(_selectionRect);
            return;
        }

        CaptureMouse();
    }

    private void FinishAnnotation()
    {
        if (_annotTool is not { } tool) return;

        switch (tool)
        {
            case AnnotationTool.Rect:
            case AnnotationTool.Ellipse:
            case AnnotationTool.Highlight:
            case AnnotationTool.Mosaic:
            {
                double x = Math.Min(_annotStart.X, _annotLast.X);
                double y = Math.Min(_annotStart.Y, _annotLast.Y);
                double w = Math.Abs(_annotLast.X - _annotStart.X);
                double h = Math.Abs(_annotLast.Y - _annotStart.Y);
                if (w < 2 || h < 2) return;
                _annotations.Add(new Annotation
                {
                    Tool = tool, X = x, Y = y, W = w, H = h,
                    Color = _annotColor, Thickness = _annotThickness, Dashed = _annotDashed,
                });
                break;
            }
            case AnnotationTool.Arrow:
                // 三点：起点、弯曲控制点（初始=中点，拖中部点可弯曲）、终点
                _arrowDragT = 0.5;   // 新箭头重置中点参数（弦中点=0.5）
                _annotations.Add(new Annotation
                {
                    Tool = AnnotationTool.Arrow,
                    Points = new[]
                    {
                        _annotStart.X, _annotStart.Y,
                        (_annotStart.X + _annotLast.X) / 2, (_annotStart.Y + _annotLast.Y) / 2,
                        _annotLast.X, _annotLast.Y,
                    },
                    Color = _annotColor, Thickness = _annotThickness, Dashed = _annotDashed, Arrow = _annotArrowStyle,
                });
                break;
            case AnnotationTool.Pen:
                if (_annotPenPts.Count < 2) return;
                {
                    var pts = new double[_annotPenPts.Count * 2];
                    for (int i = 0; i < _annotPenPts.Count; i++)
                    {
                        pts[i * 2] = _annotPenPts[i].X;
                        pts[i * 2 + 1] = _annotPenPts[i].Y;
                    }
                    _annotations.Add(new Annotation
                    {
                        Tool = AnnotationTool.Pen, Points = pts,
                        Color = _annotColor, Thickness = _annotThickness, Dashed = _annotDashed,
                    });
                }
                break;
            default:
                return;
        }

        _selectedIndex = _annotations.Count - 1;   // 先选中再刷新 → 画完立即显示控制点
        RefreshAnnotationLayer();
        ShowToolbar(_selectionRect);
    }

    // ================= 标注模型辅助 =================

    private static Annotation CloneAnnot(Annotation a) => new()
    {
        Tool = a.Tool, X = a.X, Y = a.Y, W = a.W, H = a.H,
        Points = a.Points, Text = a.Text, Number = a.Number,
        Color = a.Color, Thickness = a.Thickness,
        Dashed = a.Dashed, Arrow = a.Arrow, CornerRadius = a.CornerRadius,
    };

    /// <summary>基于现有标注构造新实例（init-only 模型专用），未指定的字段沿用原值。</summary>
    private static Annotation MakeAnnot(Annotation a,
        double? x = null, double? y = null, double? w = null, double? h = null,
        double[]? points = null, double? corner = null, double? thickness = null)
        => new()
        {
            Tool = a.Tool,
            X = x ?? a.X, Y = y ?? a.Y, W = w ?? a.W, H = h ?? a.H,
            Points = points ?? a.Points, Text = a.Text, Number = a.Number,
            Color = a.Color, Thickness = thickness ?? a.Thickness,
            Dashed = a.Dashed, Arrow = a.Arrow, CornerRadius = corner ?? a.CornerRadius,
        };

    private Rect GetAnnotBounds(Annotation a) => AnnotationHandles.Bounds(a);

    /// <summary>点到线段的最短距离（物理像素）。</summary>
    private int? HitTestAnnotation(Point relPhysical)
    {
        const double tol = 5;
        for (int i = _annotations.Count - 1; i >= 0; i--)
        {
            if (AnnotationHandles.HitShape(_annotations[i], relPhysical, tol)) return i;
        }
        return null;
    }

    private static Annotation WithColorThickness(Annotation a, Color? color = null, double? thickness = null)
    {
        return new Annotation
        {
            Tool = a.Tool, X = a.X, Y = a.Y, W = a.W, H = a.H,
            Points = a.Points, Text = a.Text, Number = a.Number,
            Color = color ?? a.Color, Thickness = thickness ?? a.Thickness,
            Dashed = a.Dashed, Arrow = a.Arrow, CornerRadius = a.CornerRadius,
        };
    }

    // ================= 控制点系统 =================

    /// <summary>
    /// 生成选中标注的控制点（物理像素，相对选区左上角）：
    /// 矩形/高亮/马赛克 = 12 点（4 角=对角缩放、4 边中点=单边拉伸、4 内角=圆角）；
    /// 椭圆 = 8 点；箭头 = 3 点（尾 / 弯曲 / 头）；文字/序号 = 本体移动 + 4 角缩放。
    /// </summary>
    /// <summary>[DEBUG] 控制点到可见曲线（P0→C→头部根部）的距离，用于验证控制点是否精确骑线。</summary>
    private static double ArrowHandleCurveDist(Annotation a, AnnotHandle h)
    {
        if (a.Points is not { Length: >= 6 }) return double.NaN;
        double x0 = a.Points[0], y0 = a.Points[1], cx = a.Points[2], cy = a.Points[3], x2 = a.Points[4], y2 = a.Points[5];
        double hsh = Math.Max(8, Math.Max(1, a.Thickness) * 4);
        double dxh = x2 - cx, dyh = y2 - cy;
        double dlh = Math.Sqrt(dxh * dxh + dyh * dyh);
        double hrxx = dlh > 1e-6 ? x2 - dxh / dlh * hsh : x2;
        double hryy = dlh > 1e-6 ? y2 - dyh / dlh * hsh : y2;
        double px = h.Pos.X, py = h.Pos.Y;
        double best = double.MaxValue;
        for (int i = 0; i <= 64; i++)
        {
            double t = i / 64.0, u = 1 - t;
            double qx = u * u * x0 + 2 * t * u * cx + t * t * hrxx;
            double qy = u * u * y0 + 2 * t * u * cy + t * t * hryy;
            double d = Math.Sqrt((qx - px) * (qx - px) + (qy - py) * (qy - py));
            if (d < best) best = d;
        }
        return best;
    }

    private static double ArrowApexX(double t, double x0, double cx, double x2)
    {
        double u = 1 - t;
        return u * u * x0 + 2 * t * u * cx + t * t * x2;
    }

    private static double ArrowApexY(double t, double y0, double cy, double y2)
    {
        double u = 1 - t;
        return u * u * y0 + 2 * t * u * cy + t * t * y2;
    }

    private void RefreshAnnotationLayer()
    {
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        _annotLayer.Children.Clear();
        _floatLayer.Children.Clear();
        double dip = _scale;

        // 标注层定位到选区
        var local = ToLocalRect(_selectionRect);
        Canvas.SetLeft(_annotLayer, local.Left);
        Canvas.SetTop(_annotLayer, local.Top);
        _annotLayer.Width = Math.Max(0, local.Width);
        _annotLayer.Height = Math.Max(0, local.Height);
        _annotLayer.Visibility = Visibility.Visible;

        // 已固化标注：每个标注用自身颜色/粗细（选中后改一个不影响其他）
        foreach (var a in _annotations)
        {
            AnnotationPainter.Draw(_annotLayer, a, dip, dip);
        }

        // 进行中绘制（用当前工具颜色/粗细）
        if (_annotDrawing && _annotTool is { } tool)
        {
            Annotation? tmp = null;
            switch (tool)
            {
                case AnnotationTool.Rect:
                case AnnotationTool.Mosaic:
                case AnnotationTool.Ellipse:
                case AnnotationTool.Highlight:
                    tmp = new Annotation
                    {
                        Tool = tool, Color = _annotColor, Thickness = _annotThickness, Dashed = _annotDashed, Arrow = _annotArrowStyle,
                        X = Math.Min(_annotStart.X, _annotLast.X), Y = Math.Min(_annotStart.Y, _annotLast.Y),
                        W = Math.Abs(_annotLast.X - _annotStart.X), H = Math.Abs(_annotLast.Y - _annotStart.Y),
                    };
                    break;
                case AnnotationTool.Arrow:
                    tmp = new Annotation
                    {
                        Tool = tool, Color = _annotColor, Thickness = _annotThickness, Dashed = _annotDashed, Arrow = _annotArrowStyle,
                        Points = new[]
                        {
                            _annotStart.X, _annotStart.Y,
                            (_annotStart.X + _annotLast.X) / 2, (_annotStart.Y + _annotLast.Y) / 2,
                            _annotLast.X, _annotLast.Y,
                        },
                    };
                    break;
                case AnnotationTool.Pen:
                    if (_annotPenPts.Count >= 2)
                    {
                        var pts = new double[_annotPenPts.Count * 2];
                        for (int i = 0; i < _annotPenPts.Count; i++) { pts[i * 2] = _annotPenPts[i].X; pts[i * 2 + 1] = _annotPenPts[i].Y; }
                        tmp = new Annotation { Tool = tool, Color = _annotColor, Thickness = _annotThickness, Dashed = _annotDashed, Arrow = _annotArrowStyle, Points = pts };
                    }
                    break;
            }
            if (tmp is not null) AnnotationPainter.Draw(_annotLayer, tmp, dip, dip);
        }

        // 选中框 + 控制点（拖动中隐藏：只画标注本体，保证跟手；松手后恢复）
        if (!_dragLive && _selectedIndex is { } sel && sel >= 0 && sel < _annotations.Count)
        {
            AnnotationPainter.DrawHandles(_annotLayer, _annotations[sel], dip, dip, _arrowDragT);

            // [DEBUG] 控制点坐标标注（箭头附"距可见曲线距离"，d≈0 即精确骑线）
            foreach (var h in AnnotationHandles.Get(_annotations[sel], _arrowDragT))
            {
                var pos = h.Pos;
                string dbgTxt = $"({pos.X:0},{pos.Y:0})";
                if (h.Kind == HandleKind.ArrowPt && _annotations[sel].Tool == AnnotationTool.Arrow)
                    dbgTxt += $" d{ArrowHandleCurveDist(_annotations[sel], h):0.#}";
                var dbg = new System.Windows.Controls.TextBlock
                {
                    Text = dbgTxt,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xC0)),
                };
                Canvas.SetLeft(dbg, pos.X * dip + 5);
                Canvas.SetTop(dbg, pos.Y * dip - 12);
                _annotLayer.Children.Add(dbg);
            }
        }

        UpdateFloatBar();
        _lastRefreshMs = sw.Elapsed.TotalMilliseconds;
    }

    private void UpdateFloatBar()
    {
        _floatLayer.Children.Clear();
        _floatLayer.Visibility = Visibility.Collapsed;

        if (_selectedIndex is not { } si || si < 0 || si >= _annotations.Count) return;

        var a = _annotations[si];
        double scale = _scale;
        var selColor = a.Color;

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x22, 0x22, 0x22)) };

        // 样式下拉（箭头=样式+线型；其余=线型），作用于选中标注
        if (a.Tool != AnnotationTool.Highlight && a.Tool != AnnotationTool.Mosaic)
        {
            var styleBtn = new Button
            {
                Content = new TextBlock
                {
                    Text = "▾",
                    Foreground = Brushes.White,
                    FontSize = 12 * scale,
                    Margin = new Thickness(2 * scale, 0, 2 * scale, 0),
                },
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(5 * scale, 1 * scale, 5 * scale, 1 * scale),
                Cursor = Cursors.Arrow,
                ToolTip = "样式（线型/箭头形状）",
            };
            var styleMenu = a.Tool == AnnotationTool.Arrow
                ? AnnotationStyleMenus.BuildArrowMenu(_annotArrowStyle, _annotDashed, OnArrowStyleApply)
                : AnnotationStyleMenus.BuildLineStyleMenu(_annotDashed, OnLineStyleApply);
            styleBtn.Click += (_, _) => { styleMenu.PlacementTarget = styleBtn; styleMenu.IsOpen = true; };
            styleBtn.ContextMenu = styleMenu;
            panel.Children.Add(styleBtn);
        }

        foreach (var c in AnnotColors)
        {
            bool active = c == selColor;
            var cb = new Border
            {
                Width = 14 * scale, Height = 14 * scale,
                Background = new SolidColorBrush(c),
                CornerRadius = new CornerRadius(2 * scale),
                Margin = new Thickness(2 * scale),
                BorderBrush = active ? Brushes.White : Brushes.Transparent,
                BorderThickness = new Thickness((active ? 2 : 1) * scale),
                Cursor = Cursors.Hand,
                Tag = c,
            };
            var target = si;
            cb.MouseLeftButtonDown += (_, _) =>
            {
                _annotations[target] = WithColorThickness(_annotations[target], color: (Color)cb.Tag);
                RefreshAnnotationLayer();
                ShowToolbar(_selectionRect);
            };
            panel.Children.Add(cb);
        }

        var minus = MakeFloatBtn("−", scale);
        minus.Click += (_, _) => AdjustSelectedThickness(-1);
        panel.Children.Add(minus);

        var num = new TextBlock
        {
            Text = $"{a.Thickness:0.#}",
            Foreground = Brushes.White,
            FontSize = 11 * scale,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2 * scale, 0, 2 * scale, 0),
        };
        panel.Children.Add(num);

        var plus = MakeFloatBtn("+", scale);
        plus.Click += (_, _) => AdjustSelectedThickness(1);
        panel.Children.Add(plus);

        var del = MakeFloatBtn("✕", scale);
        del.Click += (_, _) =>
        {
            if (_selectedIndex is { } dsi && dsi >= 0 && dsi < _annotations.Count)
            {
                _annotations.RemoveAt(dsi);
                _selectedIndex = null;
                RefreshAnnotationLayer();
                ShowToolbar(_selectionRect);
            }
        };
        panel.Children.Add(del);

        var host = new Border
        {
            Child = panel,
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x22, 0x22, 0x22)),
            CornerRadius = new CornerRadius(4 * scale),
            Padding = new Thickness(3 * scale),
        };

        var b = GetAnnotBounds(a);
        // 浮动样式条与截图工具条左端对齐、固定在工具条下方（不再跟随标注水平位置）
        double fx = Canvas.GetLeft(_toolbarHost);
        double fy;
        if (_toolbarHost.Visibility == Visibility.Visible)
        {
            fy = Canvas.GetTop(_toolbarHost) + _toolbarHost.ActualHeight + 8 * scale;
            if (fy + 44 * scale > MonitorHelper.VirtualScreen.Height)
                fy = b.Y * scale - 34 * scale;
        }
        else
        {
            fx = (b.X + b.Width / 2) * scale;
            fy = b.Y * scale - 34 * scale;
        }
        if (fy < 0) fy = (b.Y + b.Height) * scale + 8 * scale;

        Canvas.SetLeft(host, fx);
        Canvas.SetTop(host, fy);
        _floatLayer.Children.Add(host);
        _floatLayer.Visibility = Visibility.Visible;
    }

    private Button MakeFloatBtn(string glyph, double scale) => new()
    {
        Content = glyph,
        Margin = new Thickness(1 * scale, 0, 1 * scale, 0),
        Padding = new Thickness(6 * scale, 1 * scale, 6 * scale, 1 * scale),
        FontSize = 11 * scale,
        Foreground = Brushes.White,
        Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
        BorderThickness = new Thickness(0),
        Cursor = Cursors.Arrow,
    };

    private void AdjustSelectedThickness(double delta)
    {
        if (_selectedIndex is not { } si || si < 0 || si >= _annotations.Count) return;
        var a = _annotations[si];
        double nt = Math.Clamp(a.Thickness + delta, 1, 20);
        if (Math.Abs(nt - a.Thickness) > 0.01)
        {
            _annotations[si] = WithColorThickness(a, thickness: nt);
            RefreshAnnotationLayer();
        }
    }

    /// <summary>圆角矩形路径（DIP 坐标，预览层用）。</summary>
    private void AddArrowPreview(double x0, double y0, double cx, double cy, double x2, double y2, Brush brush, double stroke, bool dashed = false, ArrowStyle style = ArrowStyle.Solid)
    {
        var color = ((SolidColorBrush)brush).Color;

        // 终点切线方向 = P2 - C
        double dx = x2 - cx, dy = y2 - cy;
        double len = Math.Sqrt(dx * dx + dy * dy);
        double ux, uy, nx, ny;
        if (len < 1e-6) { ux = 0; uy = 0; nx = 1; ny = 0; }
        else { ux = dx / len; uy = dy / len; nx = -uy; ny = ux; }

        double h = Math.Max(8, stroke * 4);
        double hw = h * 0.42;
        double hrx = x2 - ux * h, hry = y2 - uy * h;

        // 杆：二次贝塞尔曲线（终点 = 头部根部，实心箭头一体不穿头）
        var shaft = new StreamGeometry();
        using (var g = shaft.Open())
        {
            g.BeginFigure(new Point(x0, y0), false, false);
            g.QuadraticBezierTo(new Point(cx, cy), new Point(hrx, hry), true, false);
        }
        shaft.Freeze();
        var sp = new Path { Data = shaft, Stroke = brush, StrokeThickness = stroke };
        if (dashed) sp.StrokeDashArray = new DoubleCollection { 4, 3 };
        _annotLayer.Children.Add(sp);

        switch (style)
        {
            case ArrowStyle.Solid:
            {
                var head = new StreamGeometry();
                using (var g = head.Open())
                {
                    g.BeginFigure(new Point(hrx + nx * hw, hry + ny * hw), true, true);
                    g.LineTo(new Point(x2, y2), true, false);
                    g.LineTo(new Point(hrx - nx * hw, hry - ny * hw), true, false);
                }
                head.Freeze();
                _annotLayer.Children.Add(new Path { Data = head, Fill = brush });
                break;
            }
            case ArrowStyle.Line:
            {
                double hl = Math.Max(8, stroke * 3.5);
                double hlw = hl * 0.38;
                double lhrx = x2 - ux * hl, lhry = y2 - uy * hl;
                double lw = Math.Max(1.5, stroke * 0.8);
                var lp = new SolidColorBrush(color);
                _annotLayer.Children.Add(new Line { X1 = x2, Y1 = y2, X2 = lhrx + nx * hlw, Y2 = lhry + ny * hlw, Stroke = lp, StrokeThickness = lw });
                _annotLayer.Children.Add(new Line { X1 = x2, Y1 = y2, X2 = lhrx - nx * hlw, Y2 = lhry - ny * hlw, Stroke = lp, StrokeThickness = lw });
                break;
            }
            case ArrowStyle.Double:
            {
                double h2 = h * 0.6, hw2 = hw * 0.6;
                double dx0 = x0 - cx, dy0 = y0 - cy;
                double l0 = Math.Sqrt(dx0 * dx0 + dy0 * dy0);
                double u0x = l0 > 1e-6 ? dx0 / l0 : 0, u0y = l0 > 1e-6 ? dy0 / l0 : 0;
                double n0x = -u0y, n0y = u0x;
                var head = new StreamGeometry();
                using (var g = head.Open())
                {
                    g.BeginFigure(new Point(hrx + nx * hw, hry + ny * hw), true, true);
                    g.LineTo(new Point(x2, y2), true, false);
                    g.LineTo(new Point(hrx - nx * hw, hry - ny * hw), true, false);
                    g.BeginFigure(new Point(x0 - u0x * h2 + n0x * hw2, y0 - u0y * h2 + n0y * hw2), true, true);
                    g.LineTo(new Point(x0, y0), true, false);
                    g.LineTo(new Point(x0 - u0x * h2 - n0x * hw2, y0 - u0y * h2 - n0y * hw2), true, false);
                }
                head.Freeze();
                _annotLayer.Children.Add(new Path { Data = head, Fill = brush });
                break;
            }
        }
    }

    // ================= 完成 / 取消 =================

    private void Finish(CaptureAction action)
    {
        var rect = CurrentRect();
        if (rect.Width < 1 || rect.Height < 1) { Cancel(); return; }

        BitmapSource bmp = _shot.Crop(rect)!;
        if (bmp is null) { Cancel(); return; }

        // 有标注：固化渲染进截图（裁剪 + 标注合成为最终图）
        if (_annotations.Count > 0)
        {
            var rendered = AnnotationRenderer.Render(bmp, _annotations);
            if (rendered is not null) bmp = rendered;
        }

        Completed?.Invoke(new CaptureResult(bmp, rect, action));
        Close();
    }

    private void ResetSelection()
    {
        _hasSelection = false;
        _selecting = false;
        _selectionRect = default;
        _annotations.Clear();
        _selectedIndex = null;
        _annotTool = null;
        _dragLive = false;
        _activeHandle = null;
        _draggingAnnot = false;
        _arrowLivePt = null;
        _annotLayer.Children.Clear();
        _floatLayer.Children.Clear();
        _toolbarHost.Visibility = Visibility.Collapsed;
        HideAll();
        RefreshCursorVisuals(GetCursorPhysical());
    }

    private void Cancel()
    {
        Cancelled?.Invoke();
        Close();
    }

    private void HideAll()
    {
        // 必须用虚拟屏幕物理尺寸，不能用 Scene.Width/Height（构造期 PlaceOverVirtualScreen 尚未执行，为 NaN）
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        var vs = MonitorHelper.VirtualScreen;
        group.Children.Add(new RectangleGeometry(new Rect(0, 0, vs.Width, vs.Height)));
        _mask.Data = group;
        _mask.Visibility = Visibility.Visible;

        _border.Visibility = Visibility.Collapsed;
        _selHandleLayer.Visibility = Visibility.Collapsed;
        _hud.Visibility = Visibility.Collapsed;
        _toolbarHost.Visibility = Visibility.Collapsed;
        _annotLayer.Visibility = Visibility.Collapsed;
        _floatLayer.Visibility = Visibility.Collapsed;
    }
}
