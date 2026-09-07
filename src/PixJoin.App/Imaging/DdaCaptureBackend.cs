using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace PixJoin.App.Imaging;

/// <summary>
/// Desktop Duplication 截图后端（Vortice.Direct3D11 / DXGI，MIT）。
/// 遍历所有显卡适配器与输出，逐输出 DuplicateOutput 抓帧，
/// 按各输出 DesktopCoordinates（桌面物理坐标）拼接到虚拟屏幕坐标系。
/// 捕获内容与 WGC 同底层（DXGI Desktop Duplication），可覆盖
/// DirectComposition / WinUI3 / 视频 / 游戏等 GDI 截不出的内容；
/// 独占全屏游戏与 RDP 会话受系统级限制（WGC 同样如此）。
/// </summary>
public sealed class DdaCaptureBackend : ICaptureBackend
{
    public string Name => "DDA";

    /// <summary>最近一次失败的详细原因（诊断用）。</summary>
    public static string? LastError { get; private set; }

    public bool IsAvailable
    {
        get
        {
            try { return CreateHardwareDevice(null) is not null; }
            catch (Exception ex) { LastError = "IsAvailable: " + ex.Message; return false; }
        }
    }

    public ScreenShot? CaptureVirtualScreen()
    {
        LastError = null;
        try
        {
            return CaptureCore();
        }
        catch (Exception ex)
        {
            LastError = "Capture: " + ex.Message;
            return null;
        }
    }

    private ScreenShot? CaptureCore()
    {
        MonitorHelper.Refresh();
        var vs = MonitorHelper.VirtualScreen;
        int vw = (int)vs.Width, vh = (int)vs.Height;
        int vx = (int)vs.Left, vy = (int)vs.Top;
        if (vw <= 0 || vh <= 0) return null;

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        // 系统默认适配器创建的设备在主流机器上最稳（枚举 adapter 传 D3D11CreateDevice 反而失败，
        // 见 probe：AMD 780M 枚举项带 adapter 全失败、null adapter 成功且能正常 DuplicateOutput）。
        using var device = CreateHardwareDevice(null);
        if (device is null)
        {
            LastError = "D3D11 设备创建失败";
            return null;
        }

        using var canvas = new Bitmap(vw, vh, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.Black);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        }

        bool any = false;
        for (int a = 0; ; a++)
        {
            using IDXGIAdapter1? adapter = SafeEnumAdapter(factory, a);
            if (adapter is null) break;

            for (int o = 0; ; o++)
            {
                using IDXGIOutput? output = SafeEnumOutput(adapter, o);
                if (output is null) break;

                var rc = output.Description.DesktopCoordinates;
                int ow = rc.Right - rc.Left, oh = rc.Bottom - rc.Top;
                if (ow <= 0 || oh <= 0) continue;

                IDXGIOutputDuplication? dup = null;
                try
                {
                    using var output1 = output.QueryInterface<IDXGIOutput1>();
                    dup = output1.DuplicateOutput(device);
                    using var frame = CaptureOneOutput(dup, device, ow, oh);
                    if (frame is null) continue;

                    int dx = rc.Left - vx, dy = rc.Top - vy;
                    using (var g = Graphics.FromImage(canvas))
                    {
                        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                        g.DrawImage(frame, dx, dy, ow, oh);
                    }
                    any = true;
                }
                catch (Exception ex)
                {
                    // 该输出不可复制（多 GPU 上设备与输出不匹配等），跳过继续
                    LastError = "output[" + a + "," + o + "]: " + ex.Message;
                }
                finally { dup?.Dispose(); }
            }
        }

        if (!any) { LastError ??= "无任何输出可抓取"; return null; }

        var bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
            canvas.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        if (bitmap.CanFreeze) bitmap.Freeze();
        return new ScreenShot(bitmap, vx, vy);
    }

    private static IDXGIAdapter1? SafeEnumAdapter(IDXGIFactory1 factory, int index)
    {
        try
        {
            var result = factory.EnumAdapters1((uint)index, out IDXGIAdapter1? adapter);
            return result.Success ? adapter : null;
        }
        catch
        {
            return null;
        }
    }

    private static IDXGIOutput? SafeEnumOutput(IDXGIAdapter1 adapter, int index)
    {
        try
        {
            var result = adapter.EnumOutputs((uint)index, out IDXGIOutput? output);
            return result.Success ? output : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>为指定适配器创建硬件 D3D11 设备；adapter 为 null 时用系统默认。</summary>
    private static ID3D11Device? CreateHardwareDevice(IDXGIAdapter1? adapter)
    {
        var levels = new[]
        {
            FeatureLevel.Level_11_0, FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0, FeatureLevel.Level_9_3,
        };
        try
        {
            var result = D3D11.D3D11CreateDevice(adapter, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                levels, out ID3D11Device device);
            return result.Success ? device : null;
        }
        catch
        {
            try
            {
                var result = D3D11.D3D11CreateDevice(adapter, DriverType.Hardware, DeviceCreationFlags.None,
                    levels, out ID3D11Device device);
                return result.Success ? device : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>抓一块输出的当前桌面帧。静止桌面可能超时，重试数次。</summary>
    private static Bitmap? CaptureOneOutput(IDXGIOutputDuplication dup, ID3D11Device device, int w, int h)
    {
        IDXGIResource? resource = null;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var result = dup.AcquireNextFrame(400, out var info, out resource);
            if (result.Success && resource is not null)
            {
                // DDA 建立初期会先返回几帧 AccumulatedFrames==0 的空黑帧（实测 2~3 帧），
                // 跳过它们直到拿到含真实桌面内容的帧。
                if (info.AccumulatedFrames > 0) break;
                dup.ReleaseFrame();
                resource.Dispose();
                resource = null;
                continue;
            }
            if (attempt == 7)
            {
                LastError = "AcquireNextFrame 超时（code=" + result.Code + "）";
                return null;
            }
        }

        try
        {
            using var desktopTex = resource.QueryInterface<ID3D11Texture2D>();
            var desc = new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            };
            using var staging = device.CreateTexture2D(desc);
            device.ImmediateContext.CopyResource(staging, desktopTex);

            var mapped = device.ImmediateContext.Map(staging, 0, MapMode.Read);
            if (mapped.DataPointer == IntPtr.Zero)
            {
                LastError = "Map 返回空指针";
                return null;
            }

            try
            {
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    // RowPitch 可能大于 w*4（行对齐），逐行拷贝
                    for (int y = 0; y < h; y++)
                    {
                        int srcPitch = (int)mapped.RowPitch;
                        CopyRow(mapped.DataPointer + y * srcPitch, bd.Scan0 + y * bd.Stride, Math.Min(srcPitch, bd.Stride));
                    }
                }
                finally { bmp.UnlockBits(bd); }
                return bmp;
            }
            finally { device.ImmediateContext.Unmap(staging, 0); }
        }
        finally { dup.ReleaseFrame(); }
    }

    private static unsafe void CopyRow(IntPtr src, IntPtr dst, int count)
    {
        if (count <= 0) return;
        Buffer.MemoryCopy((void*)src, (void*)dst, count, count);
        // DDA 桌面帧的 alpha 通道通常为 0（透明）——若直接贴图内容不可见（只显示外框），
        // 强制不透明（32bppArgb 每 4 字节第 4 字节为 alpha）。
        byte* p = (byte*)dst;
        for (int i = 3; i < count; i += 4) p[i] = 0xFF;
    }
}
