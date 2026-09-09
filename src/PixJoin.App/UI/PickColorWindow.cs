using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PixJoin.App.Imaging;
using PixJoin.App.Native;

namespace PixJoin.App.UI;

/// <summary>
/// 全局取色器（托盘「取色…」打开）：全屏冻结画面 + 像素放大镜 + 色卡，
/// 移动实时显示 HEX/RGB，单击复制并关闭，Esc 关闭。
/// </summary>
public sealed class PickColorWindow : Window
{
    private const int MagSource = 21;      // 取样边长（物理像素）
    private const int MagZoom = 12;        // 放大倍数
    private readonly ScreenShot _shot;
    private readonly Image _bg;
    private readonly Canvas _magOverlay;
    private readonly TextBlock _hexText;
    private string _hex = "#FFFFFF";

    public PickColorWindow()
    {
        var shot = ScreenCapture.CaptureVirtualScreen();
        _shot = shot;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = Cursors.Cross;

        double vsLeft = MonitorHelper.VirtualScreen.Left;
        double vsTop = MonitorHelper.VirtualScreen.Top;
        double vsW = MonitorHelper.VirtualScreen.Width;
        double vsH = MonitorHelper.VirtualScreen.Height;
        Left = vsLeft; Top = vsTop; Width = vsW; Height = vsH;

        var root = new Canvas();

        // 冻结画面（轻微压暗，突出取色）
        _bg = new Image
        {
            Source = shot.Bitmap,
            Stretch = Stretch.None,
            Opacity = 0.6,
        };
        Canvas.SetLeft(_bg, -shot.OriginX);
        Canvas.SetTop(_bg, -shot.OriginY);
        root.Children.Add(_bg);

        // 放大镜容器
        _magOverlay = new Canvas { IsHitTestVisible = false };
        root.Children.Add(_magOverlay);

        // 色卡 + HEX
        _hexText = new TextBlock
        {
            FontSize = 14,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x00, 0x00, 0x00)),
            Padding = new Thickness(8, 3, 8, 3),
            IsHitTestVisible = false,
        };
        root.Children.Add(_hexText);

        Content = root;

        MouseMove += (_, _) => UpdateMagnifier();
        MouseLeftButtonDown += (_, _) =>
        {
            try { Clipboard.SetText(_hex); } catch { }
            Close();
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        Loaded += (_, _) =>
        {
            Focus();
            UpdateMagnifier();
        };
    }

    private void UpdateMagnifier()
    {
        Win32.GetPhysicalCursorPos(out var p);
        double pcx = p.X, pcy = p.Y;

        // 放大镜取当前物理坐标周围的冻结帧
        var src = new Rect(pcx - MagSource / 2, pcy - MagSource / 2, MagSource, MagSource);
        var crop = _shot.Crop(src);

        _magOverlay.Children.Clear();
        double viewW = MagSource * MagZoom, viewH = MagSource * MagZoom;
        _magOverlay.Width = viewW;
        _magOverlay.Height = viewH;

        if (crop is not null)
        {
            var img = new Image { Source = crop, Width = viewW, Height = viewH, IsHitTestVisible = false };
            _magOverlay.Children.Add(img);

            // 像素网格
            var gridBrush = new SolidColorBrush(Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF));
            for (int i = 1; i < MagSource; i++)
            {
                double x = i * MagZoom;
                _magOverlay.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = viewH, Stroke = gridBrush, StrokeThickness = 1 });
            }
            for (int i = 1; i < MagSource; i++)
            {
                double y = i * MagZoom;
                _magOverlay.Children.Add(new Line { X1 = 0, X2 = viewW, Y1 = y, Y2 = y, Stroke = gridBrush, StrokeThickness = 1 });
            }

            // 中心像素高亮
            var center = new Rectangle
            {
                Width = MagZoom, Height = MagZoom,
                Fill = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xE0, 0x66)),
                Stroke = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xE0, 0x66)),
                StrokeThickness = 1,
            };
            Canvas.SetLeft(center, viewW / 2 - MagZoom / 2);
            Canvas.SetTop(center, viewH / 2 - MagZoom / 2);
            _magOverlay.Children.Add(center);
        }

        // 中心像素色
        var px = _shot.Crop(new Rect(pcx, pcy, 1, 1));
        if (px is not null)
        {
            var buf = new byte[4];
            var conv = px.Format == PixelFormats.Bgra32 || px.Format == PixelFormats.Pbgra32
                ? px
                : new FormatConvertedBitmap(px, PixelFormats.Bgra32, null, 0);
            conv.CopyPixels(buf, 4, 0);
            _hex = $"#{buf[2]:X2}{buf[1]:X2}{buf[0]:X2}";
            _hexText.Text = $"{_hex}  RGB({buf[2]:D3},{buf[1]:D3},{buf[0]:D3})  单击复制 · Esc 退出";
            _hexText.Background = new SolidColorBrush(Color.FromArgb(0xDD, (byte)(buf[2] / 3), (byte)(buf[1] / 3), (byte)(buf[0] / 3)));
        }

        // 定位：放大镜在光标右下（贴边翻转）
        double mw = _magOverlay.Width + 20, mh = _magOverlay.Height + 40;
        double mx = pcx + 18 - MonitorHelper.VirtualScreen.Left;
        double my = pcy + 18 - MonitorHelper.VirtualScreen.Top;
        if (mx + mw > Width) mx = pcx - mw - 18 - MonitorHelper.VirtualScreen.Left;
        if (my + mh > Height) my = pcy - mh - 18 - MonitorHelper.VirtualScreen.Top;
        Canvas.SetLeft(_magOverlay, mx);
        Canvas.SetTop(_magOverlay, my);

        // 色卡定位在放大镜下方
        Canvas.SetLeft(_hexText, mx);
        Canvas.SetTop(_hexText, my + _magOverlay.Height + 6);
    }
}
