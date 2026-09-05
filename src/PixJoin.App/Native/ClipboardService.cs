using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media.Imaging;

namespace PixJoin.App.Native;

/// <summary>
/// 剪贴板写入：带自动重试，规避剪贴板被其它进程占用时的瞬时失败
/// （WPF Clipboard.SetText 直接抛 CLIPBRD_E_CANT_OPEN，导致未处理异常）。
/// 文本用 Win32 原生 CF_UNICODETEXT 写入（最稳）；图片用 WPF 写入 + 重试。
/// </summary>
public static class ClipboardService
{
    /// <summary>写文本到剪贴板。失败自动重试（最多 5 次、间隔递增），全部失败返回 false。</summary>
    public static bool TrySetText(string text, int maxAttempts = 5)
    {
        if (string.IsNullOrEmpty(text)) return false;

        for (int i = 0; i < maxAttempts; i++)
        {
            if (i > 0) Thread.Sleep(80 * i);

            if (!Win32.OpenClipboard(IntPtr.Zero)) continue;   // 被占用：稍后重试

            try
            {
                Win32.EmptyClipboard();
                IntPtr hGlobal = Marshal.StringToHGlobalUni(text);
                if (Win32.SetClipboardData(Win32.CF_UNICODETEXT, hGlobal) != IntPtr.Zero)
                    return true;                                // 成功：内存归剪贴板所有，勿释放
                Marshal.FreeHGlobal(hGlobal);                   // 失败：释放自建内存
            }
            finally
            {
                Win32.CloseClipboard();
            }
        }
        return false;
    }

    /// <summary>写图片到剪贴板。失败自动重试，全部失败返回 false。</summary>
    public static bool TrySetImage(BitmapSource image, int maxAttempts = 5)
    {
        if (image is null) return false;

        for (int i = 0; i < maxAttempts; i++)
        {
            if (i > 0) Thread.Sleep(80 * i);
            try
            {
                System.Windows.Clipboard.SetImage(image);
                return true;
            }
            catch (ExternalException) { }   // 含 COMException（CLIPBRD_E_CANT_OPEN 等）
        }
        return false;
    }
}
