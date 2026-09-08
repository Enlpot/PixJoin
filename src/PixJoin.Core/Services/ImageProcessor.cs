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

    // ---------------- 内部实现 ----------------

    private static BitmapSource Mutate(BitmapSource src, Action<IImageProcessingContext> op)
    {
        using var img = ToImage(src);
        img.Mutate(op);
        return ToBitmapSource(img, src.DpiX, src.DpiY);
    }

    private static Image<Bgra32> ToImage(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        int stride = w * 4;
        var px = new byte[stride * h];
        src.CopyPixels(px, stride, 0);
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
