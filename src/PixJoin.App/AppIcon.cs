using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace PixJoin.App;

/// <summary>程序图标：两个交叠的圆角方块，表达"Pin + Join"。运行时生成，避免引入二进制资源。</summary>
internal static class AppIcon
{
    // Icon.FromHandle 依赖底层位图保持存活，这里钉住引用防止被 GC 回收后图标失效
    private static readonly List<Bitmap> Bitmaps = new();

    public static Icon Create(int size = 32)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            float u = size / 32f;
            float r = 5 * u;

            // 后块（深色，稍靠右下）
            using (var brush = new SolidBrush(Color.FromArgb(255, 0x00, 0x7A, 0x6B)))
                g.FillPath(brush, RoundedRect(11 * u, 11 * u, 18 * u, 18 * u, r));

            // 前块（亮色，靠左上）
            using (var brush = new SolidBrush(Color.FromArgb(255, 0x00, 0xE5, 0xC0)))
                g.FillPath(brush, RoundedRect(3 * u, 3 * u, 18 * u, 18 * u, r));

            // 前块描边，保证小尺寸下轮廓清晰
            using var pen = new Pen(Color.FromArgb(200, 0x04, 0x30, 0x2B), Math.Max(1, 1.2f * u));
            g.DrawPath(pen, RoundedRect(3 * u, 3 * u, 18 * u, 18 * u, r));
        }

        Bitmaps.Add(bmp);
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        path.AddArc(x, y, r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        return path;
    }
}
