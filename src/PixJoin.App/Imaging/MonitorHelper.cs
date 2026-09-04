using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using PixJoin.App.Native;

namespace PixJoin.App.Imaging;

/// <summary>显示器信息（物理像素坐标）。</summary>
public sealed record MonitorInfo(
    IntPtr Handle,
    int Left,
    int Top,
    int Width,
    int Height,
    double Scale,
    bool IsPrimary)
{
    public Rect PhysicalBounds => new(Left, Top, Width, Height);

    public override string ToString() => $"{Width}x{Height}@({Left},{Top}) x{Scale:0.##}";
}

/// <summary>
/// 多显示器 / DPI 查询助手。进程声明为 Per-Monitor V2，
/// 因此 EnumDisplayMonitors 返回的是物理像素，GetDpiForMonitor 返回各显示器真实缩放比。
/// </summary>
public static class MonitorHelper
{
    private static List<MonitorInfo> _monitors = new();
    private static Rect _virtualScreen;

    static MonitorHelper() => Refresh();

    public static IReadOnlyList<MonitorInfo> Monitors => _monitors;

    /// <summary>整个虚拟屏幕的包围盒（物理像素）。</summary>
    public static Rect VirtualScreen => _virtualScreen;

    public static void Refresh()
    {
        var list = new List<MonitorInfo>();

        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref Win32.RECT rc, IntPtr _) =>
        {
            var mi = new Win32.MONITORINFOEX { cbSize = Marshal.SizeOf<Win32.MONITORINFOEX>() };
            Win32.GetMonitorInfo(hMonitor, ref mi);

            double scale = 1.0;
            if (Win32.GetDpiForMonitor(hMonitor, Win32.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
                scale = dpiX / 96.0;

            list.Add(new MonitorInfo(
                hMonitor,
                mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Width, mi.rcMonitor.Height,
                scale,
                (mi.dwFlags & 1) != 0));

            return true;
        }, IntPtr.Zero);

        // 理论上不会为空，但保险起见回退到虚拟屏幕尺寸
        if (list.Count == 0)
        {
            list.Add(new MonitorInfo(IntPtr.Zero, 0, 0,
                Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN),
                Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN), 1.0, true));
        }

        _monitors = list;

        // 虚拟屏幕：优先取系统指标（与 GetDC(NULL) 抓图的坐标原点一致），
        // 再用各显示器包围盒兜底，避免指标在某些多屏布局下返回 0。
        int vx = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int vy = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int vw = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
        int vh = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);

        if (vw <= 0 || vh <= 0)
        {
            double l = list.Min(m => m.Left), t = list.Min(m => m.Top);
            double r = list.Max(m => m.Left + m.Width), b = list.Max(m => m.Top + m.Height);
            vx = (int)l; vy = (int)t; vw = (int)(r - l); vh = (int)(b - t);
        }

        _virtualScreen = new Rect(vx, vy, vw, vh);
    }

    public static MonitorInfo MonitorAtPhysicalPoint(double x, double y)
    {
        foreach (var m in _monitors)
            if (x >= m.Left && x < m.Left + m.Width && y >= m.Top && y < m.Top + m.Height)
                return m;

        // 落在缝隙里时取最近的显示器
        return _monitors.OrderBy(m =>
        {
            double dx = Math.Max(m.Left - x, Math.Max(x - (m.Left + m.Width), 0));
            double dy = Math.Max(m.Top - y, Math.Max(y - (m.Top + m.Height), 0));
            return dx * dx + dy * dy;
        }).First();
    }

    /// <summary>某物理像素点所在显示器的 DPI 缩放比（用于把物理尺寸换算为 WPF 的 DIP）。</summary>
    public static double ScaleAtPhysicalPoint(double x, double y) => MonitorAtPhysicalPoint(x, y).Scale;
}
