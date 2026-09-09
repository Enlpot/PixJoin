using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixJoin.App.Imaging;
using PixJoin.App.Native;

namespace PixJoin.App.UI;

/// <summary>
/// 长截图（滚动捕获拼接）：点击目标窗口 → 自动逐屏滚动抓帧 → 底部/顶部条带匹配找重叠 → 无缝拼接。
/// Esc 或滚动到底自动停止，完成后回调长图（供贴图）。
/// </summary>
public sealed class ScrollCaptureWindow : Window
{
    public event Action<BitmapSource>? Completed;

    private const int StripRows = 160;      // 重叠匹配条带行数
    private const int SampleStep = 8;       // 行比较降采样步长（像素列）
    private const int ScrollDelta = -400;   // 每次滚动量（负=向下）
    private const int ScrollWaitMs = 200;   // 滚动后等待渲染
    private const int NoOverlapLimit = 2;   // 连续无重叠次数上限

    private readonly BitmapSource _initialShot;
    private IntPtr _targetWindow;
    private Rect _targetRect;                    // 目标窗口物理矩形
    private readonly TextBlock _hud;
    private readonly List<BitmapSource> _frames = new();
    private bool _stop;

    public ScrollCaptureWindow(BitmapSource initialShot)
    {
        _initialShot = initialShot;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = Cursors.Hand;
        Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x00, 0x00, 0x00));

        double vsLeft = MonitorHelper.VirtualScreen.Left;
        double vsTop = MonitorHelper.VirtualScreen.Top;
        double vsW = MonitorHelper.VirtualScreen.Width;
        double vsH = MonitorHelper.VirtualScreen.Height;
        Left = vsLeft; Top = vsTop; Width = vsW; Height = vsH;

        var root = new Grid();
        var img = new Image { Source = initialShot, Stretch = Stretch.None, Opacity = 0.85 };
        root.Children.Add(img);

        _hud = new TextBlock
        {
            Text = "长截图：点击要滚动的窗口区域开始自动滚动捕获\nEsc 停止并完成拼接",
            FontSize = 16,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x00, 0x00, 0x00)),
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 0, 0),
        };
        root.Children.Add(_hud);
        Content = root;

        MouseLeftButtonDown += (_, _) => { if (!_stop) _ = StartScrollAsync(); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _stop = true;
                Close();
            }
        };
    }

    /// <summary>点击处命中窗口 → 抓初始帧 → 开始自动滚动循环。</summary>
    private async Task StartScrollAsync()
    {
        Win32.GetPhysicalCursorPos(out var p);
        _targetWindow = FindTargetWindow(new Point(p.X, p.Y));
        if (_targetWindow == IntPtr.Zero)
        {
            _hud.Text = "未命中窗口，请点击目标窗口内部";
            return;
        }
        Win32.GetWindowRect(_targetWindow, out var r);
        _targetRect = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

        var first = ScreenCapture.CaptureRegion(_targetRect)?.Bitmap;
        if (first is null || first.PixelWidth < 20 || first.PixelHeight < 20)
        {
            _hud.Text = "窗口区域为空，无法开始";
            return;
        }
        _frames.Clear();
        _frames.Add(first);
        _noOverlapCount = 0;
        _hud.Text = $"正在滚动捕获：{_frames.Count} 帧…（Esc 停止）";

        while (!_stop)
        {
            Win32.PostMessage(_targetWindow, Win32.WM_MOUSEWHEEL,
                new IntPtr((ScrollDelta & 0xFFFF) << 16), IntPtr.Zero);
            await Task.Delay(ScrollWaitMs);
            if (_stop) break;

            var shot = ScreenCapture.CaptureRegion(_targetRect);
            if (shot is null) break;

            var cur = shot.Bitmap;
            var prev = _frames[^1];
            int overlap = FindOverlap(prev, cur);

            if (overlap < 10)
            {
                _noOverlapCount++;
                if (_noOverlapCount >= NoOverlapLimit) break;   // 到底或内容无变化
                continue;
            }

            _noOverlapCount = 0;
            _frames.Add(cur);
            _hud.Text = $"正在滚动捕获：{_frames.Count} 帧…（Esc 停止）";
        }

        Finish();
    }

    private int _noOverlapCount;

    /// <summary>找 prev 底部与 cur 顶部的重叠行数；SSD 最小，失败返回 -1。</summary>
    private static int FindOverlap(BitmapSource prev, BitmapSource cur)
    {
        int pw = prev.PixelWidth, ph = prev.PixelHeight;
        int cw = cur.PixelWidth, ch = cur.PixelHeight;
        if (pw < 10 || cw < 10 || ph < 20 || ch < 20) return -1;

        // 行均值降采样：prev 底部条带、cur 顶部条带
        int rows = Math.Min(StripRows, Math.Min(ph, ch) / 2);
        int cols = Math.Min(pw, cw) / SampleStep;
        var prevRows = RowMeans(prev, ph - rows, rows, cols);
        var curRows = RowMeans(cur, 0, rows, cols);
        if (prevRows is null || curRows is null) return -1;

        int bestK = -1;
        double bestScore = double.MaxValue;
        for (int k = 10; k <= rows; k++)
        {
            double score = 0;
            for (int i = 0; i < k; i++)
            {
                var a = prevRows[rows - k + i];
                var b = curRows[i];
                for (int j = 0; j < cols; j++)
                {
                    double d = a[j] - b[j];
                    score += d * d;
                }
            }
            score /= k * cols;
            if (score < bestScore) { bestScore = score; bestK = k; }
        }

        // 阈值：匹配差过大视为无重叠（页面跳变/动画）
        return bestScore <= 900.0 ? bestK : -1;
    }

    /// <summary>取区域行均值（每 SampleStep 列取一像素，BGR 亮度）。</summary>
    private static float[][]? RowMeans(BitmapSource src, int startRow, int rows, int cols)
    {
        var conv = src.Format == PixelFormats.Bgra32 || src.Format == PixelFormats.Pbgra32
            ? src
            : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth;
        var buf = new byte[w * rows * 4];
        try
        {
            conv.CopyPixels(new Int32Rect(0, startRow, w, rows), buf, w * 4, 0);
        }
        catch { return null; }

        var result = new float[rows][];
        for (int r = 0; r < rows; r++)
        {
            var row = new float[cols];
            int baseIdx = r * w * 4;
            for (int j = 0; j < cols; j++)
            {
                int idx = baseIdx + j * SampleStep * 4;
                row[j] = buf[idx] * 0.114f + buf[idx + 1] * 0.587f + buf[idx + 2] * 0.299f;
            }
            result[r] = row;
        }
        return result;
    }

    /// <summary>拼接所有帧（重叠对齐）→ 完成回调。</summary>
    private void Finish()
    {
        if (_stop && _frames.Count <= 1)
        {
            Completed?.Invoke(_frames[0]);
            Close();
            return;
        }

        try
        {
            var bmp = BuildCanvas();
            Completed?.Invoke(bmp);
        }
        catch (Exception ex)
        {
            _hud.Text = "拼接失败：" + ex.Message;
        }
        Close();
    }

    /// <summary>字节数组拼接：Bgra32 全程，最终生成 BitmapSource。</summary>
    private BitmapSource BuildCanvas()
    {
        int totalW = _frames[0].PixelWidth;
        int totalH = _frames[0].PixelHeight;
        var overlaps = new int[_frames.Count - 1];
        for (int i = 1; i < _frames.Count; i++)
        {
            int ov = Math.Max(10, FindOverlap(_frames[i - 1], _frames[i]));
            overlaps[i - 1] = ov;
            totalH += _frames[i].PixelHeight - ov;
        }

        var dst = new byte[totalW * totalH * 4];
        int writeY = 0;
        for (int i = 0; i < _frames.Count; i++)
        {
            var px = ToBgra(_frames[i]);
            int w = _frames[i].PixelWidth, h = _frames[i].PixelHeight;
            int startRow = i == 0 ? 0 : overlaps[i - 1];
            for (int y = startRow; y < h; y++)
            {
                Buffer.BlockCopy(px, (y * w) * 4, dst, ((writeY + y - startRow) * totalW) * 4, w * 4);
            }
            writeY += h - startRow;
        }

        return BitmapSource.Create(totalW, totalH, 96, 96, PixelFormats.Bgra32, null, dst, totalW * 4);
    }

    private static byte[] ToBgra(BitmapSource src)
    {
        var conv = src.Format == PixelFormats.Bgra32 || src.Format == PixelFormats.Pbgra32
            ? src
            : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var buf = new byte[w * h * 4];
        conv.CopyPixels(buf, w * 4, 0);
        return buf;
    }

    /// <summary>点击处面积最小的可见顶层窗口（排除自身/工具窗口）。</summary>
    private static IntPtr FindTargetWindow(System.Windows.Point cursor)
    {
        IntPtr best = IntPtr.Zero;
        double bestArea = double.MaxValue;
        Win32.EnumWindows((hWnd, _) =>
        {
            if (hWnd == IntPtr.Zero || !Win32.IsWindowVisible(hWnd)) return true;
            if (!Win32.GetWindowRect(hWnd, out var r)) return true;
            if (r.Left >= r.Right || r.Top >= r.Bottom) return true;
            if (cursor.X < r.Left || cursor.X >= r.Right || cursor.Y < r.Top || cursor.Y >= r.Bottom) return true;

            var cls = new System.Text.StringBuilder(256);
            Win32.GetClassName(hWnd, cls, 256);
            string cc = cls.ToString();
            if (cc is "Shell_TrayWnd" or "Progman" or "WorkerW" or "CiceroUIWndFrame" or "SysShadow" or "ToolbarWindow32")
                return true;

            double area = (double)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area < bestArea)
            {
                best = hWnd;
                bestArea = area;
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }
}
