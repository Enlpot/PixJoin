using System;
using System.Runtime.InteropServices;

namespace PixJoin.App.Native;

/// <summary>本项目用到的 Win32 API 汇总。所有坐标均为「物理像素」虚拟屏幕坐标系。</summary>
internal static class Win32
{
    // ---------- 结构体 ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rc, IntPtr data);

    // ---------- 常量 ----------
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    public const uint SRCCOPY = 0x00CC0020;
    public const uint CAPTUREBLT = 0x40000000;

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_LAYERED = 0x00080000;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_HIDEWINDOW = 0x0080;

    public const int WM_HOTKEY = 0x0312;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 3;

    // ---- 剪贴板 ----
    public const uint CF_UNICODETEXT = 13;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;
    public const int VK_LWIN = 0x5B;
    public const int VK_ESCAPE = 0x1B;

    public const int MDT_EFFECTIVE_DPI = 0;

    // DPI 感知枚举（PROCESS_DPI_AWARENESS / DPI_AWARENESS）
    public const int DPI_AWARENESS_INVALID = -1;
    public const int DPI_AWARENESS_UNAWARE = 0;
    public const int DPI_AWARENESS_SYSTEM_AWARE = 1;
    public const int DPI_AWARENESS_PER_MONITOR_AWARE = 2;

    // DPI_AWARENESS_CONTEXT 哨兵值（GetThreadDpiAwarenessContext 返回，负数表示"已感知"）
    public const int DPI_AWARENESS_CONTEXT_UNAWARE = -1;
    public const int DPI_AWARENESS_CONTEXT_SYSTEM_AWARE = -2;
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = -3;
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    // ---------- user32 ----------
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>取鼠标位置（始终返回物理像素，不受进程 DPI 感知模式影响）。</summary>
    [DllImport("user32.dll")]
    public static extern bool GetPhysicalCursorPos(out POINT lpPoint);

    // ---- 剪贴板（原生写入，规避 WPF Clipboard 在占用时的 CLIPBRD_E_CANT_OPEN） ----
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    /// <summary>
    /// 取显示器信息。必须显式 <c>CharSet.Unicode</c>：缺省 P/Invoke 会解析到 <c>GetMonitorInfoA</c>，
    /// 与本项目 104 字节的 <c>MONITORINFOEX</c>（<c>szDevice = WCHAR[32]</c>）不匹配，
    /// 导致 <c>cbSize</c> 错位而返回 <c>false</c>、<c>rcMonitor</c> 全 0，最终把贴图缩成 8×8。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    // ---------- shcore ----------
    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ---------- DPI 感知诊断 ----------
    [DllImport("user32.dll")]
    public static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    public static extern int GetAwarenessFromDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("shcore.dll")]
    public static extern int GetProcessDpiAwareness(IntPtr hprocess, out int value);

    // ---------- gdi32 / user32 截图 ----------
    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h,
                                     IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    // ---------- 工具 ----------
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>把扩展样式位打开。</summary>
    public static void AddExStyle(IntPtr hwnd, int style)
    {
        int cur = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((cur & style) == 0) SetWindowLong(hwnd, GWL_EXSTYLE, cur | style);
    }

    public static void RemoveExStyle(IntPtr hwnd, int style)
    {
        int cur = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((cur & style) != 0) SetWindowLong(hwnd, GWL_EXSTYLE, cur & ~style);
    }

    // ---------- 全局键盘钩子（ESC 关闭贴图用） ----------
    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;

    [DllImport("user32.dll")]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
}
