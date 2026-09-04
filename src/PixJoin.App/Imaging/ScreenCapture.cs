using System;
using System.Windows;
using System.Windows.Media.Imaging;
using PixJoin.App.Native;

namespace PixJoin.App.Imaging;

/// <summary>一次屏幕抓取的结果：位图 + 它在虚拟屏幕中的物理像素原点。</summary>
public sealed class ScreenShot
{
    public ScreenShot(BitmapSource bitmap, int originX, int originY)
    {
        Bitmap = bitmap;
        OriginX = originX;
        OriginY = originY;
    }

    public BitmapSource Bitmap { get; }

    /// <summary>位图左上角对应的虚拟屏幕物理坐标。</summary>
    public int OriginX { get; }

    public int OriginY { get; }

    /// <summary>按虚拟屏幕物理像素坐标裁剪出一块区域。</summary>
    public BitmapSource? Crop(Rect physicalRect)
    {
        int rx = (int)Math.Floor(physicalRect.Left) - OriginX;
        int ry = (int)Math.Floor(physicalRect.Top) - OriginY;
        int rw = (int)Math.Ceiling(physicalRect.Width);
        int rh = (int)Math.Ceiling(physicalRect.Height);

        // 与位图求交，避免越界
        int x0 = Math.Max(0, rx);
        int y0 = Math.Max(0, ry);
        int x1 = Math.Min(Bitmap.PixelWidth, rx + rw);
        int y1 = Math.Min(Bitmap.PixelHeight, ry + rh);

        int w = x1 - x0;
        int h = y1 - y0;
        if (w <= 0 || h <= 0) return null;

        var cropped = new CroppedBitmap(Bitmap, new Int32Rect(x0, y0, w, h));
        cropped.Freeze();
        return cropped;
    }
}

/// <summary>
/// 屏幕抓取（GDI BitBlt）。
/// 进程为 Per-Monitor V2 感知，GetDC(NULL) + BitBlt 得到的是设备物理像素，
/// 不含 DPI 虚拟化缩放，因此 100% / 125% / 150% / 200% 下选区与像素都能一一对应。
/// </summary>
public static class ScreenCapture
{
    /// <summary>抓取整个虚拟屏幕（物理像素）。</summary>
    public static ScreenShot CaptureVirtualScreen()
    {
        int x = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int y = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int w = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
        int h = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);

        if (w <= 0 || h <= 0)
        {
            var vs = MonitorHelper.VirtualScreen;
            x = (int)vs.Left; y = (int)vs.Top; w = (int)vs.Width; h = (int)vs.Height;
        }

        return new ScreenShot(Blit(x, y, w, h), x, y);
    }

    /// <summary>直接抓取指定物理像素区域（不缓存全屏，用于独立调用）。</summary>
    public static ScreenShot CaptureRegion(Rect physicalRect)
    {
        int x = (int)Math.Floor(physicalRect.Left);
        int y = (int)Math.Floor(physicalRect.Top);
        int w = (int)Math.Ceiling(physicalRect.Width);
        int h = (int)Math.Ceiling(physicalRect.Height);
        return new ScreenShot(Blit(x, y, w, h), x, y);
    }

    private static BitmapSource Blit(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) throw new ArgumentOutOfRangeException(nameof(w), "截图区域无效");

        IntPtr hdcScreen = Win32.GetDC(IntPtr.Zero);
        IntPtr hdcMem = Win32.CreateCompatibleDC(hdcScreen);
        IntPtr hBitmap = Win32.CreateCompatibleBitmap(hdcScreen, w, h);
        IntPtr hOld = Win32.SelectObject(hdcMem, hBitmap);

        // CAPTUREBLT：保证能抓到分层窗口 / 硬件叠加层的内容
        bool ok = Win32.BitBlt(hdcMem, 0, 0, w, h, hdcScreen, x, y, Win32.SRCCOPY | Win32.CAPTUREBLT);
        if (!ok) Win32.BitBlt(hdcMem, 0, 0, w, h, hdcScreen, x, y, Win32.SRCCOPY);

        Win32.SelectObject(hdcMem, hOld);

        BitmapSource? result = null;
        try
        {
            result = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            // CreateBitmapSourceFromHBitmap 会拷贝像素数据，这里的 GDI 对象必须手动释放
            Win32.DeleteObject(hBitmap);
            Win32.DeleteDC(hdcMem);
            Win32.ReleaseDC(IntPtr.Zero, hdcScreen);
        }

        if (result is null) throw new InvalidOperationException("屏幕抓取失败");
        if (result.CanFreeze) result.Freeze();
        return result;
    }
}
