using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PixJoin.App.Imaging;
using PixJoin.App.Native;
using PixJoin.Core.Models;

namespace PixJoin.App.UI;

/// <summary>
/// 覆盖全屏的「反馈层」：组合体外框 / 角标 + 吸附预览 + 对齐参考线。
/// 窗口为 click-through（WS_EX_TRANSPARENT），不拦截任何鼠标操作。
/// </summary>
public sealed class GuideLayer : PhysicalCanvasWindow
{
    private static readonly Color Accent = Color.FromRgb(0x00, 0xE5, 0xC0);
    private readonly Canvas _groupLayer = new() { IsHitTestVisible = false };
    private readonly Canvas _snapLayer = new() { IsHitTestVisible = false };

    public GuideLayer() : base(clickThrough: true, noActivate: true)
    {
        Scene.Children.Add(_groupLayer);
        Scene.Children.Add(_snapLayer);
    }

    /// <summary>重新铺满虚拟屏幕（显示器布局变化后调用）。</summary>
    public void Place() => PlaceOverVirtualScreen();

    /// <summary>把反馈层重新提到所有置顶窗口之上（贴图窗口是后建的，会盖住它）。</summary>
    private void RaiseToTopMost()
    {
        if (Handle == IntPtr.Zero) return;
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    /// <summary>重绘所有组合体的外框与角标。</summary>
    public void UpdateGroups(IEnumerable<(Rect Bounds, int Count)> groups)
    {
        var origin = MonitorHelper.VirtualScreen;
        _groupLayer.Children.Clear();
        RaiseToTopMost();

        foreach (var (bounds, count) in groups)
        {
            var local = new Rect(bounds.Left - origin.Left, bounds.Top - origin.Top, bounds.Width, bounds.Height);
            double scale = MonitorHelper.ScaleAtPhysicalPoint(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

            // 外框
            var outline = new Rectangle
            {
                Width = Math.Max(1, local.Width),
                Height = Math.Max(1, local.Height),
                Stroke = new SolidColorBrush(Accent),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 6, 4 },
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(outline, local.Left);
            Canvas.SetTop(outline, local.Top);
            _groupLayer.Children.Add(outline);

            // 四角标记
            double tick = 10 * scale;
            foreach (var (tx, ty) in new[]
                     {
                         (local.Left, local.Top),
                         (local.Right - tick, local.Top),
                         (local.Left, local.Bottom - tick),
                         (local.Right - tick, local.Bottom - tick),
                     })
            {
                var corner = new Rectangle
                {
                    Width = tick, Height = tick,
                    Fill = new SolidColorBrush(Color.FromArgb(0xAA, Accent.R, Accent.G, Accent.B)),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(corner, tx);
                Canvas.SetTop(corner, ty);
                _groupLayer.Children.Add(corner);
            }

            // 角标：成员数量
            var badge = new Border
            {
                Background = new SolidColorBrush(Accent),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(5 * scale, 1 * scale, 5 * scale, 1 * scale),
                Child = new TextBlock
                {
                    Text = $"已组合 {count}",
                    Foreground = Brushes.Black,
                    FontSize = 11 * scale,
                    FontWeight = FontWeights.SemiBold,
                },
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(badge, local.Left);
            Canvas.SetTop(badge, local.Top - 18 * scale);
            _groupLayer.Children.Add(badge);
        }
    }

    /// <summary>显示吸附预览：目标高亮 + 贴合位置预览 + 对齐参考线。</summary>
    public void ShowSnap(Rect preview, Rect target, IReadOnlyList<GuideLine> guides)
    {
        var origin = MonitorHelper.VirtualScreen;
        _snapLayer.Children.Clear();

        Rect lp = new(preview.Left - origin.Left, preview.Top - origin.Top, preview.Width, preview.Height);
        Rect lt = new(target.Left - origin.Left, target.Top - origin.Top, target.Width, target.Height);

        // 目标高亮
        var targetRect = new Rectangle
        {
            Width = Math.Max(1, lt.Width), Height = Math.Max(1, lt.Height),
            Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)),
            StrokeThickness = 2,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(targetRect, lt.Left);
        Canvas.SetTop(targetRect, lt.Top);
        _snapLayer.Children.Add(targetRect);

        // 预览位置
        var previewRect = new Rectangle
        {
            Width = Math.Max(1, lp.Width), Height = Math.Max(1, lp.Height),
            Stroke = new SolidColorBrush(Accent),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x22, Accent.R, Accent.G, Accent.B)),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(previewRect, lp.Left);
        Canvas.SetTop(previewRect, lp.Top);
        _snapLayer.Children.Add(previewRect);

        // 对齐参考线
        foreach (var g in guides)
        {
            var line = new Line
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x40, 0x81)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                IsHitTestVisible = false,
            };

            if (g.IsVertical)
            {
                double x = g.Position - origin.Left;
                line.X1 = x; line.X2 = x;
                line.Y1 = Math.Min(g.Start, lp.Top + origin.Top) - origin.Top;
                line.Y2 = Math.Max(g.End, lp.Bottom + origin.Top) - origin.Top;
            }
            else
            {
                double y = g.Position - origin.Top;
                line.Y1 = y; line.Y2 = y;
                line.X1 = Math.Min(g.Start, lp.Left + origin.Left) - origin.Left;
                line.X2 = Math.Max(g.End, lp.Right + origin.Left) - origin.Left;
            }

            _snapLayer.Children.Add(line);
        }
    }

    public void ClearSnap() => _snapLayer.Children.Clear();

    public void ClearAll()
    {
        _groupLayer.Children.Clear();
        _snapLayer.Children.Clear();
    }
}
