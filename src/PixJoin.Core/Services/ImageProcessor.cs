using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixJoin.Core.Services;

/// <summary>
/// 贴图图像处理：灰度 / 反色 / 模糊 / 锐化 / 马赛克（像素级，自实现零依赖），
/// 旋转 / 翻转（WPF TransformedBitmap，无损）。
/// 输出保持输入尺寸与 DPI，透明像素参与处理（BGRA）。
/// </summary>
public static class ImageProcessor
{
    // ---------------- 像素级操作 ----------------

    public static BitmapSource ToGrayscale(BitmapSource src) =>
        ProcessPixels(src, (b, g, r, a, x, y) => {
            byte v = (byte)((r * 299 + g * 587 + b * 114) / 1000);
            return (v, v, v, a);
        });

    public static BitmapSource Invert(BitmapSource src) =>
        ProcessPixels(src, (b, g, r, a, x, y) => ((byte)(255 - b), (byte)(255 - g), (byte)(255 - r), a));

    /// <summary>3×3 均值模糊（保留透明度）。</summary>
    public static BitmapSource Blur(BitmapSource src) =>
        Convolve(src, new double[] { 1, 1, 1, 1, 1, 1, 1, 1, 1 }, 1.0 / 9.0);

    /// <summary>3×3 锐化（中心增强）。</summary>
    public static BitmapSource Sharpen(BitmapSource src) =>
        Convolve(src, new double[] { 0, -1, 0, -1, 5, -1, 0, -1, 0 }, 1.0);

    /// <summary>把矩形区域像素化为 blockSize 色块（物理像素坐标；越界自动裁剪）。</summary>
    public static BitmapSource Pixelate(BitmapSource src, Rect region, int blockSize)
    {
        if (blockSize < 2) blockSize = 2;
        int w = src.PixelWidth, h = src.PixelHeight;
        var (px, stride) = CopyBgra(src);
        int x0 = Math.Max(0, (int)Math.Floor(region.X));
        int y0 = Math.Max(0, (int)Math.Floor(region.Y));
        int x1 = Math.Min(w, (int)Math.Ceiling(region.X + region.Width));
        int y1 = Math.Min(h, (int)Math.Ceiling(region.Y + region.Height));
        if (x1 <= x0 || y1 <= y0) return src;

        for (int by = y0; by < y1; by += blockSize)
        for (int bx = x0; bx < x1; bx += blockSize)
        {
            // 取块内平均色
            int ex = Math.Min(x1, bx + blockSize), ey = Math.Min(y1, by + blockSize);
            long sb = 0, sg = 0, sr = 0, sa = 0;
            int n = 0;
            for (int y = by; y < ey; y++)
            for (int x = bx; x < ex; x++)
            {
                int i = y * stride + x * 4;
                sb += px[i]; sg += px[i + 1]; sr += px[i + 2]; sa += px[i + 3];
                n++;
            }
            if (n == 0) continue;
            byte ab = (byte)(sb / n), ag = (byte)(sg / n), ar = (byte)(sr / n), aa = (byte)(sa / n);
            for (int y = by; y < ey; y++)
            for (int x = bx; x < ex; x++)
            {
                int i = y * stride + x * 4;
                px[i] = ab; px[i + 1] = ag; px[i + 2] = ar; px[i + 3] = aa;
            }
        }
        return FromBgra(px, w, h, stride, src);
    }

    // ---------------- 旋转 / 翻转（无损） ----------------

    public static BitmapSource Rotate(BitmapSource src, int degrees)
    {
        if (degrees == 0) return src;
        var tb = new TransformedBitmap(src, new RotateTransform(degrees));
        tb.Freeze();
        return tb;
    }

    public static BitmapSource FlipHorizontal(BitmapSource src) => Flip(src, flipX: true);

    public static BitmapSource FlipVertical(BitmapSource src) => Flip(src, flipY: true);

    private static BitmapSource Flip(BitmapSource src, bool flipX = false, bool flipY = false)
    {
        var tb = new TransformedBitmap(src, new ScaleTransform(flipX ? -1 : 1, flipY ? -1 : 1, src.PixelWidth / 2.0, src.PixelHeight / 2.0));
        tb.Freeze();
        return tb;
    }

    // ---------------- 内部实现 ----------------

    private static BitmapSource Convolve(BitmapSource src, double[] kernel, double factor)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        var (px, stride) = CopyBgra(src);
        var outp = new byte[px.Length];
        Array.Copy(px, outp, px.Length);

        for (int y = 1; y < h - 1; y++)
        for (int x = 1; x < w - 1; x++)
        {
            double sb = 0, sg = 0, sr = 0, sa = 0;
            int k = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++, k++)
            {
                int i = (y + dy) * stride + (x + dx) * 4;
                double c = kernel[k] * factor;
                sb += px[i] * c; sg += px[i + 1] * c; sr += px[i + 2] * c; sa += px[i + 3] * c;
            }
            int o = y * stride + x * 4;
            outp[o] = Clamp(sb); outp[o + 1] = Clamp(sg); outp[o + 2] = Clamp(sr); outp[o + 3] = Clamp(sa);
        }
        return FromBgra(outp, w, h, stride, src);
    }

    private static BitmapSource ProcessPixels(BitmapSource src, Func<byte, byte, byte, byte, int, int, (byte b, byte g, byte r, byte a)> fn)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        var (px, stride) = CopyBgra(src);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * stride + x * 4;
            var (b, g, r, a) = fn(px[i], px[i + 1], px[i + 2], px[i + 3], x, y);
            px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = a;
        }
        return FromBgra(px, w, h, stride, src);
    }

    private static (byte[] px, int stride) CopyBgra(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        int stride = w * 4;
        var px = new byte[stride * h];
        src.CopyPixels(px, stride, 0);
        return (px, stride);
    }

    private static BitmapSource FromBgra(byte[] px, int w, int h, int stride, BitmapSource template)
    {
        var bmp = BitmapSource.Create(w, h, template.DpiX, template.DpiY, PixelFormats.Bgra32, null, px, stride);
        bmp.Freeze();
        return bmp;
    }

    private static byte Clamp(double v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
}
