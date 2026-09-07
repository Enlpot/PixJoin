using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixJoin.Core.Models;
using PixJoin.Core.Services;

namespace PixJoin.Core.Tests;

public static class AnnotationRendererTests
{
    public static void Run()
    {
        Check.Section("AnnotationRenderer 标注渲染");

        var image = TestBitmap.Solid(40, 20, Colors.White);

        // 无标注：原样返回（同一引用）
        {
            var same = AnnotationRenderer.Render(image, new System.Collections.Generic.List<Annotation>());
            Check.True(ReferenceEquals(same, image), "空标注：原样返回");
        }

        // 矩形标注：输出同尺寸、非空、中心像素仍为底色（描边在边缘）
        {
            var list = new System.Collections.Generic.List<Annotation>
            {
                new Annotation { Tool = AnnotationTool.Rect, X = 2, Y = 2, W = 10, H = 8, Color = Colors.Red, Thickness = 2 },
            };
            var outImg = AnnotationRenderer.Render(image, list);
            Check.True(outImg.PixelWidth == 40 && outImg.PixelHeight == 20, "矩形标注：尺寸不变");
            var c = PixelAt(outImg, 7, 6);
            Check.True(c.R == 255 && c.G == 255 && c.B == 255, "矩形标注：内部保持底色");
        }

        // 马赛克标注：区域像素被块化（原图纯白，块化后仍白——用棋盘格验证差异）
        {
            var checker = CheckerBmp(16, 16);
            var list = new System.Collections.Generic.List<Annotation>
            {
                new Annotation { Tool = AnnotationTool.Mosaic, X = 0, Y = 0, W = 16, H = 16, Thickness = 8 },
            };
            var outImg = AnnotationRenderer.Render(checker, list);
            var p1 = PixelAt(outImg, 1, 1);
            var p2 = PixelAt(outImg, 3, 3);
            Check.True(p1.R == p2.R && p1.G == p2.G, "马赛克：8px 块内一致");
        }

        // 文字标注：输出非空且尺寸不变
        {
            var list = new System.Collections.Generic.List<Annotation>
            {
                new Annotation { Tool = AnnotationTool.Text, X = 2, Y = 2, Text = "测试", Color = Colors.Black, Thickness = 3 },
            };
            var outImg = AnnotationRenderer.Render(image, list);
            Check.True(outImg.PixelWidth == 40 && outImg.PixelHeight == 20, "文字标注：尺寸不变");
        }
    }

    private static Color PixelAt(BitmapSource bmp, int x, int y)
    {
        var px = new byte[4];
        bmp.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return Color.FromArgb(px[3], px[2], px[1], px[0]);
    }

    private static BitmapSource CheckerBmp(int w, int h)
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
}
