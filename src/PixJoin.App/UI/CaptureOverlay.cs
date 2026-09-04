using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PixJoin.App.Imaging;
using PixJoin.App.Native;

namespace PixJoin.App.UI;

public enum CaptureAction { Pin, Copy, Save }

public sealed record CaptureResult(BitmapSource Bitmap, Rect PhysicalRect, CaptureAction Action);

/// <summary>
/// 截图层：暗色遮罩 + 选区 + 像素级放大镜 + 尺寸 HUD + 结果工具栏。
/// 覆盖整个虚拟屏幕，单窗口处理跨显示器选区。
/// </summary>
public sealed class CaptureOverlay : PhysicalCanvasWindow
{
    private const int MagSource = 21;   // 放大镜取样边长（物理像素）
    private const int MagZoom = 6;      // 放大倍率（显示约 126×126 物理像素，较紧凑）

    private readonly ScreenShot _shot;
    private readonly Path _mask = new() { Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)) };
    private readonly Rectangle _border = new() { Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xC0)), StrokeThickness = 1 };
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

    public event Action<CaptureResult>? Completed;
    public event Action? Cancelled;

    public CaptureOverlay(ScreenShot shot) : base(clickThrough: false, noActivate: false)
    {
        _shot = shot;
        Cursor = Cursors.Cross;
        Focusable = true;

        BuildVisuals();
        PlaceOverVirtualScreen();

        MouseLeftButtonDown += OnLeftDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnLeftUp;
        MouseRightButtonDown += (_, _) => Cancel();
        KeyDown += OnKeyDown;

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

        Scene.Children.Add(_mask);
        Scene.Children.Add(_border);
        Scene.Children.Add(_hud);
        Scene.Children.Add(_magnifier);
        Scene.Children.Add(_toolbarHost);

        HideAll();
    }

    private static Point GetCursorPhysical()
    {
        Win32.GetPhysicalCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (_hasSelection) return;   // 已选完（工具栏出现）时，交给工具栏处理

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
        // 选区已定（工具栏出现）后：鼠标移动只是准备点按钮，绝不能当作新的选区终点
        if (_hasSelection) return;

        _currentPhysical = GetCursorPhysical();
        if (_selecting) UpdateSelection();
        // 无论是否在拖选都要刷新放大镜，保证它始终跟随光标
        RefreshCursorVisuals(_currentPhysical);
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
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
        ShowToolbar(rect);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_hasSelection) ResetSelection();
            else Cancel();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _hasSelection)
        {
            Finish(CaptureAction.Pin);
            e.Handled = true;
        }
        else if (e.Key == Key.C && _hasSelection)
        {
            Finish(CaptureAction.Copy);
            e.Handled = true;
        }
        else if (e.Key == Key.S && _hasSelection)
        {
            Finish(CaptureAction.Save);
            e.Handled = true;
        }
    }

    private Rect CurrentRect()
    {
        // 选区完成后使用冻结值；拖选过程中用起始点 + 当前光标实时计算
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
    }

    private Rect ToLocalRect(Rect physical)
    {
        var vs = MonitorHelper.VirtualScreen;
        return new Rect(physical.Left - vs.Left, physical.Top - vs.Top, physical.Width, physical.Height);
    }

    private void RefreshCursorVisuals(Point cursor)
    {
        if (_hasSelection) { _magnifier.Visibility = Visibility.Collapsed; return; }

        // 放大镜：以「光标所在的像素」为中心取 MagSource × MagSource 物理像素。
        // 整数对齐（Floor）保证中心像素 = 光标下的像素；旧的 cursor-X/2 会因 Crop 的
        // Floor 取整产生 0.5px 偏移，导致中心高亮像素与实际光标像素错位。
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

            // 视口尺寸跟随实际取样像素（屏幕边缘时避免露出黑底）
            _magOverlay.Width = viewW;
            _magOverlay.Height = viewH;

            // 1) 像素网格线：每个源像素一格（半透明白细线），让「周围像素」清晰可见
            var gridBrush = new SolidColorBrush(Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF));
            for (int i = 1; i < cw; i++)
            {
                double x = i * MagZoom;
                _magOverlay.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = x, X2 = x, Y1 = 0, Y2 = viewH, Stroke = gridBrush, StrokeThickness = 1,
                });
            }
            for (int i = 1; i < ch; i++)
            {
                double y = i * MagZoom;
                _magOverlay.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = 0, X2 = viewW, Y1 = y, Y2 = y, Stroke = gridBrush, StrokeThickness = 1,
                });
            }

            // 2) 中心交界线：十字准星画在中心像素的四条边界上，中心围出的就是一个完整像素
            double half = MagZoom / 2.0;
            var crossBrush = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));
            _magOverlay.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = cx - half, X2 = cx - half, Y1 = 0, Y2 = viewH, Stroke = crossBrush, StrokeThickness = 1,
            });
            _magOverlay.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = cx + half, X2 = cx + half, Y1 = 0, Y2 = viewH, Stroke = crossBrush, StrokeThickness = 1,
            });
            _magOverlay.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0, X2 = viewW, Y1 = cy - half, Y2 = cy - half, Stroke = crossBrush, StrokeThickness = 1,
            });
            _magOverlay.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0, X2 = viewW, Y1 = cy + half, Y2 = cy + half, Stroke = crossBrush, StrokeThickness = 1,
            });

            // 3) 中心像素高亮（亮黄绿边框 + 轻微填充）：中心是一个像素
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
            _magText.Text = $"#{buf[2]:X2}{buf[1]:X2}{buf[0]:X2}  ({cursor.X}, {cursor.Y})";
        }

        // 定位：优先在光标附近（右下方），且尽量放到当前选区之外，避免遮挡截图内容；
        // 只有外部放不下（屏幕边缘）时才退回靠近光标，保证放大镜始终可见。
        double mw = _magnifier.ActualWidth > 0 ? _magnifier.ActualWidth : MagSource * MagZoom + 40;
        double mh = _magnifier.ActualHeight > 0 ? _magnifier.ActualHeight : MagSource * MagZoom + 40;

        var pos = ComputeMagnifierPos(cursor, mw, mh, CurrentSelectionRect());
        var local = ToLocal(pos);
        Canvas.SetLeft(_magnifier, local.X);
        Canvas.SetTop(_magnifier, local.Y);
        _magnifier.Visibility = Visibility.Visible;
    }

    /// <summary>当前正在形成的选区（拖选中）；未开始拖或选区尺寸为 0 时返回 null。</summary>
    private Rect? CurrentSelectionRect()
    {
        if (!_selecting) return null;
        double x = Math.Min(_startPhysical.X, _currentPhysical.X);
        double y = Math.Min(_startPhysical.Y, _currentPhysical.Y);
        double w = Math.Abs(_currentPhysical.X - _startPhysical.X);
        double h = Math.Abs(_currentPhysical.Y - _startPhysical.Y);
        if (w < 1 && h < 1) return null;   // 尚未拖出有效选区，不做避让
        return new Rect(x, y, w, h);
    }

    /// <summary>
    /// 计算放大镜左上角物理坐标。
    /// 无选区：放光标右下方（贴边翻转）。
    /// 有选区：放大镜贴到选区四侧外侧（右/下/左/上），另一轴尽量贴近光标；
    /// 按"离光标最近"取第一个完整落在虚拟屏内且不与选区相交的方向；
    /// 全部放不下（屏幕边缘）时退回光标右下，保证放大镜始终可见。
    /// </summary>
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
            (s.Right + gap,        ClampY(cursor.Y - mh / 2)),  // 选区右侧
            (ClampX(cursor.X - mw / 2), s.Bottom + gap),        // 选区下侧
            (s.Left - gap - mw,    ClampY(cursor.Y - mh / 2)),  // 选区左侧
            (ClampX(cursor.X - mw / 2), s.Top - gap - mh),      // 选区上侧
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

        // 屏幕边缘放不下：退回光标右下，贴住边缘
        double fx = Math.Max(vs.Left, Math.Min(cursor.X + 24, vs.Right - mw));
        double fy = Math.Max(vs.Top, Math.Min(cursor.Y + 24, vs.Bottom - mh));
        return new Point(fx, fy);
    }

    private void ShowToolbar(Rect physicalRect)
    {
        _magnifier.Visibility = Visibility.Collapsed;

        double scale = MonitorHelper.ScaleAtPhysicalPoint(physicalRect.Left + physicalRect.Width / 2,
                                                          physicalRect.Top + physicalRect.Height / 2);
        _toolbar.Children.Clear();
        _toolbar.Children.Add(MakeButton("贴图 (Enter)", CaptureAction.Pin, scale, primary: true));
        _toolbar.Children.Add(MakeButton("复制 (C)", CaptureAction.Copy, scale));
        _toolbar.Children.Add(MakeButton("保存 (S)", CaptureAction.Save, scale));
        _toolbar.Children.Add(MakeCloseButton(scale));

        _toolbarHost.Visibility = Visibility.Visible;
        _toolbarHost.UpdateLayout();

        double bw = _toolbarHost.ActualWidth > 0 ? _toolbarHost.ActualWidth : 260;
        double bh = _toolbarHost.ActualHeight > 0 ? _toolbarHost.ActualHeight : 36;

        var local = ToLocalRect(physicalRect);
        double lx = local.Left + physicalRect.Width - bw;
        double ly = local.Bottom + 8 * scale;
        if (ly + bh > MonitorHelper.VirtualScreen.Height) ly = local.Bottom - bh - 8 * scale;
        if (lx < 0) lx = 0;

        Canvas.SetLeft(_toolbarHost, lx);
        Canvas.SetTop(_toolbarHost, ly);
    }

    private Button MakeButton(string text, CaptureAction action, double scale, bool primary = false)
    {
        var b = new Button
        {
            Content = text,
            Margin = new Thickness(3, 0, 3, 0),
            Padding = new Thickness(10 * scale, 5 * scale, 10 * scale, 5 * scale),
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

    private void Finish(CaptureAction action)
    {
        var rect = CurrentRect();
        if (rect.Width < 1 || rect.Height < 1) { Cancel(); return; }

        var bmp = _shot.Crop(rect);
        if (bmp is null) { Cancel(); return; }

        Completed?.Invoke(new CaptureResult(bmp, rect, action));
        Close();
    }

    private void ResetSelection()
    {
        _hasSelection = false;
        _selecting = false;
        _selectionRect = default;
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
        // 无选区时也要显示遮罩，否则看不出"进入了截图模式"
        // 注意：必须用虚拟屏幕物理尺寸，不能用 Scene.Width/Height —— 构造期 PlaceOverVirtualScreen 尚未执行，
        // 后者仍是 NaN，会导致遮罩几何无效、整个暗色遮罩不渲染（表现为"截图无反应"）。
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        var vs = MonitorHelper.VirtualScreen;
        group.Children.Add(new RectangleGeometry(new Rect(0, 0, vs.Width, vs.Height)));
        _mask.Data = group;
        _mask.Visibility = Visibility.Visible;

        _border.Visibility = Visibility.Collapsed;
        _hud.Visibility = Visibility.Collapsed;
        _toolbarHost.Visibility = Visibility.Collapsed;
    }
}
