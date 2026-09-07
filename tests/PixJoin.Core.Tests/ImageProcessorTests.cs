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

        // 灰度：纯红 → 亮度 = round(0.299*255) = 76
        {
            var red = TestBitmap.Solid(4, 4, Colors.Red);
            var g = ImageProcessor.ToGrayscale(red);
            var px = PixelAt(g, 1, 1);
            Check.True(px.R == 76 && px.G == 76 && px.B == 76, "灰度：纯红转亮度 76");
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
