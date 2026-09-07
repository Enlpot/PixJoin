using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace PixJoin.App.Imaging;

/// <summary>
/// 截图后端抽象。主链路 = Desktop Duplication（能抓到 DirectComposition / WinUI3 /
/// 视频 / 游戏等现代渲染内容，GDI 截这些是白屏黑屏）；失败自动降级 GDI。
/// </summary>
public interface ICaptureBackend
{
    /// <summary>后端显示名（日志 / 设置用）。</summary>
    string Name { get; }

    /// <summary>当前环境是否可用。</summary>
    bool IsAvailable { get; }

    /// <summary>抓取整个虚拟屏幕（物理像素）。失败返回 null（调用方降级）。</summary>
    ScreenShot? CaptureVirtualScreen();
}

/// <summary>GDI BitBlt 后端：兼容性最好，但 DirectComposition 内容会白屏。</summary>
public sealed class GdiCaptureBackend : ICaptureBackend
{
    public string Name => "GDI";

    public bool IsAvailable => true;

    public ScreenShot? CaptureVirtualScreen()
    {
        try { return ScreenCapture.CaptureVirtualScreenGdi(); }
        catch { return null; }
    }
}
