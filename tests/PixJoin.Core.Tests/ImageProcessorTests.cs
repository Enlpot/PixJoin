using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixJoin.Core.Models;
using PixJoin.Core.Services;

namespace PixJoin.Core.Tests;

public static class ImageProcessorTests
{
    public static void Run()
    {
        Check.Section("ImageProcessor 图像处理");

        // 灰度：纯红 → 三通道相等且显著变暗（ImageSharp BT.709 感知亮度，红≈54）
        {
            var red = TestBitmap.Solid(4, 4, Colors.Red);
            var g = ImageProcessor.ToGrayscale(red);
            var px = PixelAt(g, 1, 1);
            Check.True(px.R == px.G && px.G == px.B, "灰度：纯红后 R=G=B");
            Check.True(px.R < 120, $"灰度：纯红亮度显著降低（实际 {px.R}）");
        }

        // 反色：黑 ↔ 白
        {
            var black = TestBitmap.Solid(3, 3, Colors.Black);
            var inv = ImageProcessor.Invert(black);
            var px = PixelAt(inv, 1, 1);
            Check.True(px.R == 255 && px.G == 255 && px.B == 255, "反色：黑转白");
        }

        // 模糊：纯色不变
        {
            var blue = TestBitmap.Solid(6, 6, Colors.Blue);
            var bl = ImageProcessor.Blur(blue);
            var px = PixelAt(bl, 3, 3);
            Check.True(px.R == 0 && px.G == 0 && px.B == 255, "模糊：纯色中心不变");
        }

        // 马赛克：区域内块像素一致；不同区域平均色不同（用左右渐变）
        {
            var grad = Gradient(16, 8);   // 左黑右白
            var mos = ImageProcessor.Pixelate(grad, new Rect(0, 0, 16, 8), 4);
            var p1 = PixelAt(mos, 1, 2);   // 左块（近黑）
            var p2 = PixelAt(mos, 2, 3);   // 同一左块内
            Check.True(p1.R == p2.R && p1.G == p2.G && p1.B == p2.B, "马赛克：块内像素一致");
            var p3 = PixelAt(mos, 1, 2);
            var p4 = PixelAt(mos, 14, 2);  // 右块（近白）
            Check.True(p4.R - p3.R > 100, "马赛克：左右块平均色不同");
        }

        // 旋转 90°：宽高互换
        {
            var src = TestBitmap.Solid(20, 10, Colors.Green);
            var r = ImageProcessor.Rotate(src, 90);
            Check.True(r.PixelWidth == 10 && r.PixelHeight == 20, "旋转 90°：宽高互换");
        }

        // 水平翻转：左右对称
        {
            var grad = Gradient(8, 4);   // 左黑右白
            var f = ImageProcessor.FlipHorizontal(grad);
            var left = PixelAt(f, 1, 2).R;
            var right = PixelAt(f, 6, 2).R;
            Check.True(left > 200 && right < 50, "水平翻转：左右交换");
        }

        // 垂直翻转：上下对称
        {
            var grad = GradientVertical(4, 8);   // 上黑下白
            var f = ImageProcessor.FlipVertical(grad);
            var top = PixelAt(f, 2, 1).R;
            var bot = PixelAt(f, 2, 6).R;
            Check.True(top > 200 && bot < 50, "垂直翻转：上下交换");
        }

        // 亮度：中灰提亮/压暗方向正确
        {
            var gray = TestBitmap.Solid(4, 4, Color.FromRgb(128, 128, 128));
            var up = ImageProcessor.AdjustBrightness(gray, 1.6f);
            var down = ImageProcessor.AdjustBrightness(gray, 0.4f);
            Check.True(PixelAt(up, 2, 2).R > 150, $"亮度增强（实际 {PixelAt(up, 2, 2).R}）");
            Check.True(PixelAt(down, 2, 2).R < 90, $"亮度降低（实际 {PixelAt(down, 2, 2).R}）");
        }

        // 对比度：低对比度 → 灰面（128±差距收窄）
        {
            var grad = Gradient(8, 4);   // 左黑右白
            var low = ImageProcessor.AdjustContrast(grad, 0.2f);
            Check.True(PixelAt(low, 1, 2).R > 20 && PixelAt(low, 6, 2).R < 220, "对比度降低：两端向中间收拢");
        }

        // 饱和度：降到 0 → 彩色基本变灰（各通道接近，亮度显著下降）
        {
            var red = TestBitmap.Solid(4, 4, Colors.Red);
            var desat = ImageProcessor.AdjustSaturation(red, 0f);
            var px = PixelAt(desat, 2, 2);
            Check.True(Math.Abs(px.R - px.G) <= 4 && Math.Abs(px.G - px.B) <= 4 && px.R < 100,
                $"去饱和：纯红基本变灰（实际 {px.R},{px.G},{px.B}）");
        }

        // 裁剪：只保留区域，尺寸正确
        {
            var grad = Gradient(16, 8);
            var c = ImageProcessor.Crop(grad, new Rect(4, 2, 8, 4));
            Check.True(c.PixelWidth == 8 && c.PixelHeight == 4, "裁剪：尺寸正确");
            var left = PixelAt(c, 1, 2).R;   // 原 x=5 → 浅灰
            Check.True(left is > 60 and < 120, $"裁剪：内容对应（实际 {left}）");
        }

        // 边框：尺寸扩大、边色正确、中心内容不变
        {
            var red = TestBitmap.Solid(6, 6, Colors.Red);
            var b = ImageProcessor.AddBorder(red, 3, Colors.Yellow);
            Check.True(b.PixelWidth == 12 && b.PixelHeight == 12, "边框：尺寸 = 原图 + 2×厚度");
            Check.True(PixelAt(b, 1, 1) == Colors.Yellow, "边框：边色正确");
            Check.True(PixelAt(b, 6, 6) == Colors.Red, "边框：中心原图保留");
        }

        // 水印：右下角叠加文字（大图能放下），左上角保持原样
        {
            var white = TestBitmap.Solid(200, 120, Colors.White);
            var wm = ImageProcessor.AddWatermark(white, "PixJoin", 14, 0.8, Colors.Red);
            bool bottomChanged = false;
            for (int y = 90; y < 120; y++)
            for (int x = 130; x < 200; x++)
            {
                var p = PixelAt(wm, x, y);
                if (p.G < 250 || p.B < 250) { bottomChanged = true; break; }
            }
            Check.True(bottomChanged, "水印：右下角出现文字像素");
            Check.True(PixelAt(wm, 3, 3) == Colors.White, "水印：左上角保持原样");
        }
    }

    private static Color PixelAt(BitmapSource bmp, int x, int y)
    {
        var px = new byte[4];
        bmp.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return Color.FromArgb(px[3], px[2], px[1], px[0]);
    }

    /// <summary>4×4 棋盘格（左上黑、隔块白）。</summary>
    private static BitmapSource Checker(int w, int h)
    {
        int stride = w * 4;
        var px = new byte[stride * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            byte v = ((x / 2 + y / 2) % 2 == 0) ? (byte)0 : (byte)255;
            int i = y * stride + x * 4;
            px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255;
        }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
    }

    /// <summary>水平渐变：左黑右白。</summary>
    private static BitmapSource Gradient(int w, int h)
    {
        int stride = w * 4;
        var px = new byte[stride * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            byte v = (byte)(x * 255 / (w - 1));
            int i = y * stride + x * 4;
            px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255;
        }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
    }

    /// <summary>垂直渐变：上黑下白。</summary>
    private static BitmapSource GradientVertical(int w, int h)
    {
        int stride = w * 4;
        var px = new byte[stride * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            byte v = (byte)(y * 255 / (h - 1));
            int i = y * stride + x * 4;
            px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255;
        }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
    }
}
