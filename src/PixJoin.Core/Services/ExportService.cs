using System.IO;
using System.Windows.Media.Imaging;
using PixJoin.Core.Models;

namespace PixJoin.Core.Services;

/// <summary>
/// 导出模块：把一组贴图按相对坐标拼合为一张位图。
///
/// 质量约定：
///   - 画布尺寸 = 成员包围盒（物理像素），因此与屏幕所见 1:1 对齐；
///   - 各成员以「原始位图」为源绘制，忽略显示层透明度（需求明确：透明度只影响显示）；
///   - 贴图未被缩放时，绘制尺寸 == 原始像素尺寸，即严格 1:1 无损 blit；
///     被用户缩放过的贴图按屏幕尺寸高质量重采样（此时"所见即所得"优先于"原始像素"，
///     否则导出的图会和用户在屏幕上看到的排版完全对不上）；
///   - 输出 PNG 为无损编码，不做二次压缩；背景透明时自动裁掉四周空白。
/// </summary>
public static class ExportService
{
    public sealed record ExportOptions(
        bool TransparentBackground = true,
        bool AutoTrim = true);

    public static readonly ExportOptions Default = new();

    /// <summary>拼合一组贴图。members 为空时返回 null。</summary>
    public static BitmapSource? Compose(IReadOnlyList<Sticker> members, ExportOptions? opt = null)
    {
        opt ??= Default;
        if (members.Count == 0) return null;

        var bounds = GroupManager.UnionBounds(members.Select(m => m.Bounds));

        int w = Math.Max(1, (int)Math.Ceiling(bounds.Width));
        int h = Math.Max(1, (int)Math.Ceiling(bounds.Height));

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(visual, EdgeMode.Aliased);

        using (var dc = visual.RenderOpen())
        {
            if (!opt.TransparentBackground)
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));

            foreach (var m in members)
            {
                if (m.Image is null) continue;
                var rect = new Rect(m.X - bounds.Left, m.Y - bounds.Top, m.W, m.H);
                dc.PushOpacity(1.0); // 显示透明度不参与导出
                dc.DrawImage(m.Image, rect);
                dc.Pop();
            }
        }

        // DPI 固定 96 → 1 个 DIP == 1 个物理像素，保证不引入额外缩放
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();

        if (!opt.AutoTrim) return bmp;

        // 白底模式下整张图都不透明，裁边无意义（且会把内容边缘的白色误裁）
        var trimmed = opt.TransparentBackground ? AutoTrim(bmp) : bmp;
        trimmed?.Freeze();
        return trimmed;
    }

    /// <summary>把四周全透明的行列裁掉，裁到内容紧贴。</summary>
    public static BitmapSource AutoTrim(BitmapSource source)
    {
        int w = source.PixelWidth, h = source.PixelHeight;
        if (w == 0 || h == 0) return source;

        var bgra = new BgraBitmap(source);
        int top = 0, bottom = h - 1, left = 0, right = w - 1;

        bool RowEmpty(int y)
        {
            int rowStart = y * bgra.Stride;
            for (int x = 0; x < w; x++)
                if (bgra.Data[rowStart + x * 4 + 3] != 0) return false;
            return true;
        }

        bool ColEmpty(int x)
        {
            for (int y = 0; y < h; y++)
                if (bgra.Data[y * bgra.Stride + x * 4 + 3] != 0) return false;
            return true;
        }

        while (top <= bottom && RowEmpty(top)) top++;
        if (top > bottom) return source;              // 整张透明，原样返回
        while (bottom > top && RowEmpty(bottom)) bottom--;
        while (left <= right && ColEmpty(left)) left++;
        while (right > left && ColEmpty(right)) right--;

        int cw = right - left + 1, ch = bottom - top + 1;
        if (cw == w && ch == h) return source;

        return new CroppedBitmap(source, new Int32Rect(left, top, cw, ch));
    }

    public static byte[] ToPngBytes(BitmapSource source)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static void SavePng(BitmapSource source, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, ToPngBytes(source));
    }

    /// <summary>位图像素读取辅助（统一转 BGRA32）。</summary>
    private sealed class BgraBitmap
    {
        public byte[] Data { get; }
        public int Stride { get; }

        public BgraBitmap(BitmapSource src)
        {
            var fmt = src.Format == PixelFormats.Bgra32 || src.Format == PixelFormats.Pbgra32
                ? src.Format
                : PixelFormats.Bgra32;

            var converted = src.Format == fmt ? src : new FormatConvertedBitmap(src, fmt, null, 0);
            int w = src.PixelWidth, h = src.PixelHeight;
            Stride = ((w * fmt.BitsPerPixel + 31) / 32) * 4;
            Data = new byte[Stride * h];
            converted.CopyPixels(new Int32Rect(0, 0, w, h), Data, Stride, 0);
        }
    }
}
