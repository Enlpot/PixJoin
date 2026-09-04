using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixJoin.Core.Services;

namespace PixJoin.Core.Tests;

internal static class ExportTests
{
    /// <summary>生成"中间一块不透明、四周透明"的位图，用于验证自动裁边。</summary>
    private static BitmapSource Framed(int w, int h, int inner)
    {
        int stride = ((w * 32 + 31) / 32) * 4;
        var px = new byte[stride * h];
        int x0 = (w - inner) / 2, y0 = (h - inner) / 2;
        for (int y = y0; y < y0 + inner; y++)
        for (int x = x0; x < x0 + inner; x++)
        {
            int i = y * stride + x * 4;
            px[i + 0] = 0; px[i + 1] = 0; px[i + 2] = 255; px[i + 3] = 255; // 纯红不透明
        }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
    }

    private static byte[] Pixels(BitmapSource src)
    {
        var conv = src.Format == PixelFormats.Bgra32
            ? src
            : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int stride = ((conv.PixelWidth * 32 + 31) / 32) * 4;
        var buf = new byte[stride * conv.PixelHeight];
        conv.CopyPixels(buf, stride, 0);
        return buf;
    }

    public static void Run()
    {
        Check.Section("ExportService 导出");

        // 1. 两张并排贴图 → 画布 = 包围盒
        {
            var a = TestBitmap.Sticker(0, 0, 100, 100);
            var b = TestBitmap.Sticker(110, 0, 100, 100);
            var img = ExportService.Compose(new[] { a, b });
            Check.True(img is not null, "应能拼合出位图");
            Check.Equal(210, img!.PixelWidth, "画布宽度 = 包围盒宽度");
            Check.Equal(100, img.PixelHeight, "画布高度 = 包围盒高度");
        }

        // 2. 成员相对坐标正确落位（第二张起点 x=110）
        {
            var a = TestBitmap.Sticker(0, 0, 100, 100, color: Colors.Red);
            var b = TestBitmap.Sticker(110, 0, 100, 100, color: Color.FromRgb(0, 0, 255));
            var img = ExportService.Compose(new[] { a, b })!;
            var px = Pixels(img);
            int stride = ((img.PixelWidth * 32 + 31) / 32) * 4;

            int i50 = 50 * stride + 50 * 4;      // 第一张内部
            int i150 = 50 * stride + 150 * 4;    // 第二张内部
            Check.True(px[i50 + 2] > 200 && px[i50 + 0] < 40, "x=50 处应为第一张（红）");
            Check.True(px[i150 + 0] > 200 && px[i150 + 2] < 40, "x=150 处应为第二张（蓝）");

            int i105 = 50 * stride + 105 * 4;    // 两张之间的空隙
            Check.Equal((byte)0, px[i105 + 3], "间隙处应为透明背景");
        }

        // 3. 自动裁边：透明外框被裁掉
        {
            var s = new Models.Sticker { Image = Framed(100, 100, 20), X = 0, Y = 0, W = 100, H = 100 };
            var img = ExportService.Compose(new[] { s }, new ExportService.ExportOptions(TransparentBackground: true, AutoTrim: true));
            Check.Equal(20, img!.PixelWidth, "裁边后宽度应等于内容宽度");
            Check.Equal(20, img.PixelHeight, "裁边后高度应等于内容高度");
        }

        // 4. 关闭自动裁边时保留完整包围盒
        {
            var s = new Models.Sticker { Image = Framed(100, 100, 20), X = 0, Y = 0, W = 100, H = 100 };
            var img = ExportService.Compose(new[] { s }, new ExportService.ExportOptions(TransparentBackground: true, AutoTrim: false));
            Check.Equal(100, img!.PixelWidth, "关闭裁边应保留原始包围盒宽度");
        }

        // 5. 显示透明度不参与导出
        {
            var s = TestBitmap.Sticker(0, 0, 50, 50, color: Colors.Green);
            s.Opacity = 0.3;
            var img = ExportService.Compose(new[] { s })!;
            var px = Pixels(img);
            Check.Equal((byte)255, px[3], "导出像素应保持完全不透明（不受显示透明度影响）");
        }

        // 6. 白底导出：透明区域应被填充为不透明白色
        {
            // 用"四周透明、中间红色"的位图，透明区域即白底应露出的位置
            var s = new Models.Sticker { Image = Framed(100, 100, 20), X = 0, Y = 0, W = 100, H = 100 };
            var img = ExportService.Compose(new[] { s }, new ExportService.ExportOptions(TransparentBackground: false, AutoTrim: false))!;
            var px = Pixels(img);
            Check.Equal((byte)255, px[0], "白底模式下角落像素 B 通道应为 255");
            Check.Equal((byte)255, px[1], "白底模式下角落像素 G 通道应为 255");
            Check.Equal((byte)255, px[2], "白底模式下角落像素 R 通道应为 255");
            Check.Equal((byte)255, px[3], "白底模式下应完全不透明");
        }

        // 7. PNG 编码可用，且可再解码
        {
            var s = TestBitmap.Sticker(0, 0, 40, 30);
            var img = ExportService.Compose(new[] { s })!;
            var bytes = ExportService.ToPngBytes(img);
            Check.True(bytes.Length > 0, "应能编码出 PNG 字节");

            using var ms = new MemoryStream(bytes);
            var dec = new PngBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Check.Equal(40, dec.Frames[0].PixelWidth, "PNG 解码后宽度应与导出一致");
        }

        // 8. 缩放后的贴图按屏幕尺寸导出（所见即所得）
        {
            var s = new Models.Sticker { Image = TestBitmap.Solid(200, 200, Colors.Orange), X = 0, Y = 0, W = 100, H = 100 };
            var img = ExportService.Compose(new[] { s })!;
            Check.Equal(100, img.PixelWidth, "导出尺寸应等于屏幕显示尺寸（100×100）");
        }
    }
}
