using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PixJoin.Core.Services;

namespace PixJoin.App.UI;

/// <summary>
/// 图像处理参数弹窗（代码构建）：调整（亮度/对比度/饱和度）、边框、水印。
/// 滑块/输入实时预览（每次改动应用一次，可"撤销标注/处理"回退首版），重置恢复打开时原图。
/// </summary>
public static class ImageOpDialogs
{
    // ---------------- 亮度 / 对比度 / 饱和度 ----------------

    public static void ShowAdjust(StickerWindow window)
    {
        var orig = window.Sticker.Image;
        var dlg = new ImageOpDialogWindow("亮度 / 对比度 / 饱和度", () => window.RestorePreviewImage(orig))
        {
            Owner = window,
        };
        double b = 0, c = 0, s = 0;
        // 三滑块共享状态：每次预览都基于打开时原图组合应用三个参数（滑块独立、归零即还原该参数）
        void Apply() => window.ApplyPreviewOp(img => ImageProcessor.AdjustSaturation(
            ImageProcessor.AdjustContrast(ImageProcessor.AdjustBrightness(img, Map(b)), Map(c)), Map(s)));
        dlg.AddRow("亮度", -100, 100, 0, v => { b = v; Apply(); });
        dlg.AddRow("对比度", -100, 100, 0, v => { c = v; Apply(); });
        dlg.AddRow("饱和度", -100, 100, 0, v => { s = v; Apply(); });
        dlg.ShowDialog();
    }

    // ---------------- 边框 ----------------

    public static void ShowBorder(StickerWindow window)
    {
        var orig = window.Sticker.Image;
        var dlg = new ImageOpDialogWindow("添加边框", () => window.RestorePreviewImage(orig)) { Owner = window };
        var color = AnnotationStyleMenus.Colors[0];
        dlg.AddRow("边框粗细", 1, 60, 4, v => window.ApplyPreviewOp(b => ImageProcessor.AddBorder(b, (int)v, color)));
        dlg.AddColorRow(c =>
        {
            color = c;
            window.ApplyPreviewOp(b => ImageProcessor.AddBorder(b, (int)dlg.GetRowValue(0), c));
        });
        dlg.ShowDialog();
    }

    // ---------------- 水印 ----------------

    public static void ShowWatermark(StickerWindow window)
    {
        var orig = window.Sticker.Image;
        var dlg = new ImageOpDialogWindow("文字水印", () => window.RestorePreviewImage(orig)) { Owner = window };
        var color = AnnotationStyleMenus.Colors[0];
        string text = "PixJoin";
        double size = 40, opacity = 60;
        var tb = new TextBox { Text = text, Width = 200, Margin = new Thickness(0, 4, 0, 4) };
        tb.TextChanged += (_, _) =>
        {
            text = tb.Text;
            window.ApplyPreviewOp(b => ImageProcessor.AddWatermark(b, text, size, opacity / 100.0, color));
        };
        dlg.AddControlRow("文字", tb);
        dlg.AddRow("字号", 12, 300, 40, v =>
        {
            size = v;
            window.ApplyPreviewOp(b => ImageProcessor.AddWatermark(b, text, size, opacity / 100.0, color));
        });
        dlg.AddRow("透明度", 5, 100, 60, v =>
        {
            opacity = v;
            window.ApplyPreviewOp(b => ImageProcessor.AddWatermark(b, text, size, opacity / 100.0, color));
        });
        dlg.AddColorRow(c =>
        {
            color = c;
            window.ApplyPreviewOp(b => ImageProcessor.AddWatermark(b, text, size, opacity / 100.0, c));
        });
        dlg.ShowDialog();
    }

    private static float Map(double v) => (float)(1 + v / 100.0);   // -100→0，0→1，+100→2
}

/// <summary>通用参数弹窗：多行滑块 + 可选颜色块 + 重置/完成。reset=恢复打开时原图。</summary>
public class ImageOpDialogWindow : Window
{
    private readonly StackPanel _rows;
    private readonly List<Slider> _sliders = new();
    private readonly List<Action<double>> _handlers = new();

    public ImageOpDialogWindow(string title, Action reset)
    {
        Title = title;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        Topmost = true;

        var root = new StackPanel();
        _rows = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(_rows);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 12, 12) };
        var resetBtn = new Button { Content = "重置", Width = 72, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(4, 2, 4, 2) };
        resetBtn.Click += (_, _) => reset();
        var done = new Button { Content = "完成", Width = 72, Padding = new Thickness(4, 2, 4, 2) };
        done.Click += (_, _) => Close();
        btns.Children.Add(resetBtn);
        btns.Children.Add(done);
        root.Children.Add(btns);

        Content = root;
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    /// <summary>加一行滑块。range: -100..100 或 1..60 等；默认值 def。</summary>
    public void AddRow(string label, double min, double max, double def, Action<double> preview)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var lab = new TextBlock { Text = label, Width = 64, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        var val = new TextBlock { Text = def.ToString("0"), Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(6, 0, 0, 0) };
        var slider = new Slider { Minimum = min, Maximum = max, Value = def, Width = 180, VerticalAlignment = VerticalAlignment.Center };
        int idx = _sliders.Count;
        slider.ValueChanged += (_, e) =>
        {
            val.Text = e.NewValue.ToString("0");
            if (idx < _handlers.Count) _handlers[idx]?.Invoke(e.NewValue);
        };
        _sliders.Add(slider);
        _handlers.Add(preview);
        DockPanel.SetDock(lab, Dock.Left);
        DockPanel.SetDock(slider, Dock.Right);
        DockPanel.SetDock(val, Dock.Right);
        row.Children.Add(slider);
        row.Children.Add(val);
        row.Children.Add(lab);
        _rows.Children.Add(row);
    }

    /// <summary>加颜色行（8 色块，点击回调）。</summary>
    public void AddColorRow(Action<Color> onPick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(new TextBlock { Text = "颜色", Width = 64, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        foreach (var cc in AnnotationStyleMenus.Colors)
        {
            var b = new Border
            {
                Width = 18, Height = 18,
                Background = new SolidColorBrush(cc),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(2),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = cc,
            };
            b.MouseLeftButtonDown += (_, _) => onPick((Color)b.Tag);
            row.Children.Add(b);
        }
        _rows.Children.Add(row);
    }

    /// <summary>加任意控件行（如文字输入）。</summary>
    public void AddControlRow(string label, UIElement control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(new TextBlock { Text = label, Width = 64, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);
        _rows.Children.Add(row);
    }

    public double GetRowValue(int idx) => _sliders[idx].Value;
}
