using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixJoin.Core.Models;

namespace PixJoin.Core.Tests;

/// <summary>测试用位图工厂：生成指定尺寸 / 颜色的纯色位图。</summary>
internal static class TestBitmap
{
    public static BitmapSource Solid(int w, int h, Color color, double dpi = 96)
    {
        int stride = ((w * 32 + 31) / 32) * 4;
        var pixels = new byte[stride * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * stride + x * 4;
            pixels[i + 0] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = color.A;
        }
        return BitmapSource.Create(w, h, dpi, dpi, PixelFormats.Bgra32, null, pixels, stride);
    }

    public static Sticker Sticker(double x, double y, double w, double h, string? groupId = null, Color? color = null)
    {
        color ??= Colors.SteelBlue;
        return new Models.Sticker
        {
            Image = Solid((int)w, (int)h, color.Value),
            X = x, Y = y, W = w, H = h,
            GroupId = groupId,
        };
    }
}
