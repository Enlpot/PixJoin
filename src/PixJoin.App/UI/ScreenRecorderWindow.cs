using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PixJoin.App.Imaging;
using PixJoin.App.Native;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using Rect = System.Windows.Rect;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Rectangle = System.Windows.Shapes.Rectangle;
using Color = System.Windows.Media.Color;
using Image = SixLabors.ImageSharp.Image;

namespace PixJoin.App.UI;

/// <summary>
/// GIF 屏幕录制：全屏覆盖 → 拖拽选区 → Enter 开始 / Esc 停止并保存。
/// 10fps 定时抓屏（DXGI/Duplication 后端复用），停止后用 ImageSharp 编码为循环 GIF。
/// 帧缓存上限 240 帧（约 24 秒），达到自动停止，避免内存占用失控。
/// </summary>
public sealed class ScreenRecorderWindow : Window
{
    private const int FrameIntervalMs = 100;   // 10 fps
    private const int MaxFrames = 240;         // 上限（约 24s），防内存失控

    private readonly Rectangle _sel = new()
    {
        Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x29, 0x9D, 0xF0)),
        StrokeThickness = 1.5,
        Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x29, 0x9D, 0xF0)),
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _hud;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(FrameIntervalMs) };
    private readonly List<byte[]> _frames = new();
    private readonly List<int> _frameSizes = new();
    private Point? _down;
    private Rect _selRect;
    private int _frameW, _frameH;
    private DateTime _startTime;

    public ScreenRecorderWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = Cursors.Cross;
        Background = new SolidColorBrush(Color.FromArgb(0x10, 0x00, 0x00, 0x00));

        double vsLeft = MonitorHelper.VirtualScreen.Left;
        double vsTop = MonitorHelper.VirtualScreen.Top;
        double vsW = MonitorHelper.VirtualScreen.Width;
        double vsH = MonitorHelper.VirtualScreen.Height;
        Left = vsLeft; Top = vsTop; Width = vsW; Height = vsH;

        var root = new Grid();
        root.Children.Add(_sel);

        _hud = new TextBlock
        {
            Text = "GIF 录制：拖拽选择区域，Enter 开始 / 停止，Esc 取消",
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

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        KeyDown += OnKeyDown;
        _timer.Tick += (_, _) => CaptureFrame();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _down = e.GetPosition(this);
        _selRect = new Rect(_down.Value, new Size(0, 0));
        _sel.Visibility = Visibility.Visible;
        UpdateSel();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_down is not { } d) return;
        var cur = e.GetPosition(this);
        _selRect = new Rect(Math.Min(d.X, cur.X), Math.Min(d.Y, cur.Y),
            Math.Abs(cur.X - d.X), Math.Abs(cur.Y - d.Y));
        UpdateSel();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _down = null;
        if (_selRect.Width < 20 || _selRect.Height < 20)
        {
            _sel.Visibility = Visibility.Collapsed;
            return;
        }
        _hud.Text = $"录制区域 {_selRect.Width:0}×{_selRect.Height:0} —— Enter 开始 / 停止，Esc 取消";
    }

    private void UpdateSel()
    {
        _sel.Width = _selRect.Width;
        _sel.Height = _selRect.Height;
        Canvas.SetLeft(_sel, _selRect.X);
        Canvas.SetTop(_sel, _selRect.Y);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_timer.IsEnabled) StopAndSave();
            else if (_selRect.Width >= 20 && _selRect.Height >= 20) StartRecording();
        }
    }

    private void StartRecording()
    {
        _frames.Clear();
        _frameSizes.Clear();
        _frameW = (int)Math.Round(_selRect.Width);
        _frameH = (int)Math.Round(_selRect.Height);
        _startTime = DateTime.Now;
        _sel.Fill = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0x00, 0x00));
        _hud.Text = "● 录制中… Enter / Esc 停止并保存";
        _timer.Start();
        CaptureFrame();   // 立即抓首帧
    }

    private void CaptureFrame()
    {
        double vsLeft = MonitorHelper.VirtualScreen.Left;
        double vsTop = MonitorHelper.VirtualScreen.Top;
        var phys = new Rect(vsLeft + _selRect.X, vsTop + _selRect.Y, _selRect.Width, _selRect.Height);

        var shot = ScreenCapture.CaptureRegion(phys)?.Bitmap;
        if (shot is null) return;

        var bgra = shot.Format == PixelFormats.Bgra32 || shot.Format == PixelFormats.Pbgra32
            ? shot
            : new FormatConvertedBitmap(shot, PixelFormats.Bgra32, null, 0);
        int w = Math.Min(_frameW, bgra.PixelWidth);
        int h = Math.Min(_frameH, bgra.PixelHeight);
        var buf = new byte[w * h * 4];
        bgra.CopyPixels(new Int32Rect(0, 0, w, h), buf, w * 4, 0);
        // Bgra32 → Rgba32（交换 B/R）
        for (int i = 0; i < buf.Length; i += 4)
        {
            byte t = buf[i];
            buf[i] = buf[i + 2];
            buf[i + 2] = t;
        }
        _frames.Add(buf);
        _frameSizes.Add(w * h * 4);
        _hud.Text = $"● 录制中… {_frames.Count} 帧 / {(DateTime.Now - _startTime).TotalSeconds:0.0}s（Enter / Esc 保存）";

        if (_frames.Count >= MaxFrames)
        {
            _timer.Stop();
            _hud.Text = $"已达帧数上限（{MaxFrames}），自动停止";
            SaveGif();
        }
    }

    private void StopAndSave()
    {
        _timer.Stop();
        if (_frames.Count == 0)
        {
            Close();
            return;
        }
        SaveGif();
    }

    private void SaveGif()
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存 GIF",
                Filter = "GIF 图片 (*.gif)|*.gif",
                DefaultExt = ".gif",
                FileName = $"PixJoin_{DateTime.Now:yyyyMMdd_HHmmss}.gif",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            };
            if (dlg.ShowDialog() != true)
            {
                Close();
                return;
            }

            using var gif = new Image<Rgba32>(_frameW, _frameH);
            gif.Metadata.GetGifMetadata().RepeatCount = 0;   // 无限循环
            foreach (var buf in _frames)
            {
                using var frame = Image.LoadPixelData<Rgba32>(buf, _frameW, _frameH);
                var added = gif.Frames.AddFrame(frame.Frames.RootFrame);
                added.Metadata.GetFormatMetadata(GifFormat.Instance).FrameDelay = (int)(FrameIntervalMs / 10.0);   // 1/100s 单位
            }
            gif.Frames.RemoveFrame(0);   // 去掉初始空白帧
            gif.SaveAsGif(dlg.FileName);

            System.Windows.MessageBox.Show(
                $"GIF 已保存：{Path.GetFileName(dlg.FileName)}\n{_frames.Count} 帧 · {_frameW}×{_frameH}",
                "PixJoin", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"保存 GIF 失败：{ex.Message}", "PixJoin",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Close();
    }
}
