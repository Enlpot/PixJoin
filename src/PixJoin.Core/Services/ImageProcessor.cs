using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PixJoin.Core.Services;

/// <summary>
/// 贴图图像处理：灰度 / 反色 / 模糊 / 锐化 / 马赛克 / 旋转 / 翻转，
/// 基于 ImageSharp 4.x（SIMD 加速、Apache-2.0），透明像素参与处理（Bgra32）。
/// 输出保持输入尺寸与 DPI，不降低清晰度。
/// </summary>
public static class ImageProcessor
{
    public static BitmapSource ToGrayscale(BitmapSource src) =>
        Mutate(src, x => x.Grayscale());

    public static BitmapSource Invert(BitmapSource src) =>
        Mutate(src, x => x.Invert());

    /// <summary>高斯模糊（轻度，保留透明度）。</summary>
    public static BitmapSource Blur(BitmapSource src) =>
        Mutate(src, x => x.GaussianBlur(1.5f));

    /// <summary>高斯锐化（轻度，保留透明度）。</summary>
    public static BitmapSource Sharpen(BitmapSource src) =>
        Mutate(src, x => x.GaussianSharpen(1.0f));

    /// <summary>把矩形区域像素化为 blockSize 色块（物理像素坐标；越界自动裁剪）。</summary>
    public static BitmapSource Pixelate(BitmapSource src, Rect region, int blockSize)
    {
        if (blockSize < 2) blockSize = 2;
        int w = src.PixelWidth, h = src.PixelHeight;
        int x0 = Math.Clamp((int)Math.Floor(region.X), 0, w);
        int y0 = Math.Clamp((int)Math.Floor(region.Y), 0, h);
        int x1 = Math.Clamp((int)Math.Ceiling(region.X + region.Width), 0, w);
        int y1 = Math.Clamp((int)Math.Ceiling(region.Y + region.Height), 0, h);
        if (x1 <= x0 || y1 <= y0) return src;

        using var img = ToImage(src);
        img.Mutate(x => x.Pixelate(blockSize, new Rectangle(x0, y0, x1 - x0, y1 - y0)));
        return ToBitmapSource(img, src.DpiX, src.DpiY);
    }

    // ---------------- 旋转 / 翻转（整数倍角无损） ----------------

    public static BitmapSource Rotate(BitmapSource src, int degrees)
    {
        if (degrees == 0) return src;
        var mode = degrees switch
        {
            90 => RotateMode.Rotate90,
            180 => RotateMode.Rotate180,
            270 => RotateMode.Rotate270,
            _ => (RotateMode?)null,
        };
        if (mode is null) return src;
        return Mutate(src, x => x.Rotate(mode.Value));
    }

    public static BitmapSource FlipHorizontal(BitmapSource src) =>
        Mutate(src, x => x.Flip(FlipMode.Horizontal));

    public static BitmapSource FlipVertical(BitmapSource src) =>
        Mutate(src, x => x.Flip(FlipMode.Vertical));

    // ---------------- 亮度 / 对比度 / 饱和度（amount: 0 最弱，1 不变） ----------------

    public static BitmapSource AdjustBrightness(BitmapSource src, float amount) =>
        Mutate(src, x => x.Brightness(Math.Clamp(amount, 0.01f, 2f)));

    public static BitmapSource AdjustContrast(BitmapSource src, float amount) =>
        Mutate(src, x => x.Contrast(Math.Clamp(amount, 0.01f, 2f)));

    public static BitmapSource AdjustSaturation(BitmapSource src, float amount) =>
        Mutate(src, x => x.Saturate(Math.Clamp(amount, 0.01f, 2f)));

    // ---------------- 裁剪（物理像素区域，越界自动裁剪） ----------------

    /// <summary>按物理像素矩形裁剪（越界自动收敛到图像范围内；无效区域原样返回）。</summary>
    public static BitmapSource Crop(BitmapSource src, Rect region)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        int x0 = Math.Clamp((int)Math.Floor(region.X), 0, w);
        int y0 = Math.Clamp((int)Math.Floor(region.Y), 0, h);
        int x1 = Math.Clamp((int)Math.Ceiling(region.X + region.Width), 0, w);
        int y1 = Math.Clamp((int)Math.Ceiling(region.Y + region.Height), 0, h);
        if (x1 <= x0 || y1 <= y0) return src;

        using var img = ToImage(src);
        img.Mutate(x => x.Crop(new Rectangle(x0, y0, x1 - x0, y1 - y0)));
        return ToBitmapSource(img, src.DpiX, src.DpiY);
    }

    // ---------------- 边框（四周加纯色带，不缩放原图） ----------------

    /// <summary>四周加纯色边框（物理像素厚度；输出尺寸 = 原图 + 2×厚度，1:1 无损拷入）。</summary>
    public static BitmapSource AddBorder(BitmapSource src, int thickness, System.Windows.Media.Color color)
    {
        if (thickness <= 0) return src;
        int w = src.PixelWidth, h = src.PixelHeight;
        int nw = w + thickness * 2, nh = h + thickness * 2;
        var px = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
        {
            for (int x = 0; x < nw; x++)
            {
                int i = (y * nw + x) * 4;
                px[i] = color.B; px[i + 1] = color.G; px[i + 2] = color.R; px[i + 3] = color.A;
            }
        }
        // 与 ToImage 一致：非 BGRA 源先转换，避免字节序错乱
        var conv = src.Format == PixelFormats.Bgra32
            ? src
            : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var srcPx = new byte[w * h * 4];
        conv.CopyPixels(srcPx, w * 4, 0);
        for (int y = 0; y < h; y++)
            Array.Copy(srcPx, y * w * 4, px, ((y + thickness) * nw + thickness) * 4, w * 4);
        var bmp = BitmapSource.Create(nw, nh, src.DpiX, src.DpiY, PixelFormats.Bgra32, null, px, nw * 4);
        bmp.Freeze();
        return bmp;
    }

    // ---------------- 文字水印（WPF 绘制文字 → 透明层 → alpha 混合，右下角） ----------------

    /// <summary>右下角加半透明文字水印（不降低原图清晰度，保留透明度）。</summary>
    public static BitmapSource AddWatermark(BitmapSource src, string text, double fontSize, double opacity,
        System.Windows.Media.Color color, double margin = 16)
    {
        if (string.IsNullOrWhiteSpace(text)) return src;
        int w = src.PixelWidth, h = src.PixelHeight;

        // 1. 用 WPF 把文字画到透明位图（与源图同尺寸，文字定位右下角）
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var ft = new System.Windows.Media.FormattedText(text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"),
                fontSize,
                new SolidColorBrush(color));
            double tw = ft.Width, th = ft.Height;
            var pos = new System.Windows.Point(w - margin - tw, h - margin - th);
            if (pos.X < 0) pos.X = 0;
            if (pos.Y < 0) pos.Y = 0;
            dc.DrawText(ft, pos);
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var wmStride = w * 4;
        var wmPx = new byte[wmStride * h];
        rtb.CopyPixels(wmPx, wmStride, 0);

        // 2. 源图 Bgra32 → 与文字层 alpha 混合
        using var img = ToImage(src);
        var srcPx = new byte[wmStride * h];
        img.CopyPixelDataTo(srcPx);
        double a = Math.Clamp(opacity, 0, 1);
        for (int i = 0; i < srcPx.Length; i += 4)
        {
            byte wa = wmPx[i + 3];
            if (wa == 0) continue;
            // Pbgra32 → 预乘还原，再按水印透明度合成
            double wa2 = wa / 255.0 * a;
            double inv = 1 - wa2;
            srcPx[i] = (byte)(srcPx[i] * inv + wmPx[i] * wa2);
            srcPx[i + 1] = (byte)(srcPx[i + 1] * inv + wmPx[i + 1] * wa2);
            srcPx[i + 2] = (byte)(srcPx[i + 2] * inv + wmPx[i + 2] * wa2);
        }
        var outBmp = BitmapSource.Create(w, h, src.DpiX, src.DpiY, PixelFormats.Bgra32, null, srcPx, wmStride);
        outBmp.Freeze();
        return outBmp;
    }

    // ---------------- 内部实现 ----------------

    private static BitmapSource Mutate(BitmapSource src, Action<IImageProcessingContext> op)
    {
        using var img = ToImage(src);
        img.Mutate(op);
        return ToBitmapSource(img, src.DpiX, src.DpiY);
    }

    private static Image<Bgra32> ToImage(BitmapSource src)
    {
        // 统一转换为 Bgra32：非 BGRA 源（如 JPG 的 Bgr24）直接按 4 字节/像素拷贝会字节错位
        var conv = src.Format == PixelFormats.Bgra32
            ? src
            : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        int stride = w * 4;
        var px = new byte[stride * h];
        conv.CopyPixels(px, stride, 0);
        return Image.LoadPixelData<Bgra32>(px, w, h);
    }

    private static BitmapSource ToBitmapSource(Image<Bgra32> img, double dpiX, double dpiY)
    {
        int w = img.Width, h = img.Height;
        var px = new byte[w * 4 * h];
        img.CopyPixelDataTo(px);
        var bmp = BitmapSource.Create(w, h, dpiX, dpiY, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }
}
