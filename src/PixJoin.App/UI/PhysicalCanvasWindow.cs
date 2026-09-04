using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PixJoin.App.Imaging;
using PixJoin.App.Native;

namespace PixJoin.App.UI;

/// <summary>
/// 覆盖整个虚拟屏幕的透明顶层窗口基类。
///
/// 关键设计：内部一律用「物理像素」作图。
/// WPF 的布局单位是 DIP，渲染时会乘以窗口的 DPI 缩放比 s，
/// 因此给 Scene 施加 1/s 的 LayoutTransform 后，
/// 在 Canvas 上按物理像素摆放的子元素就会被渲染到正确的物理位置上。
/// 窗口本身的位置与尺寸也用 SetWindowPos 以物理像素设置，绕开 WPF 的 DPI 换算歧义。
/// </summary>
public abstract class PhysicalCanvasWindow : Window
{
    protected Canvas Scene { get; }

    private readonly bool _clickThrough;
    private readonly bool _noActivate;

    protected PhysicalCanvasWindow(bool clickThrough = true, bool noActivate = true)
    {
        _clickThrough = clickThrough;
        _noActivate = noActivate;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;

        Scene = new Canvas
        {
            Background = Brushes.Transparent,
            SnapsToDevicePixels = true,
            UseLayoutRounding = false,
        };
        Content = Scene;

        SourceInitialized += (_, _) => OnHwndReady();
        DpiChanged += (_, _) => PlaceOverVirtualScreen();
        Loaded += (_, _) => PlaceOverVirtualScreen();
    }

    public IntPtr Handle { get; private set; }

    private void OnHwndReady()
    {
        Handle = new WindowInteropHelper(this).Handle;

        // 不在 Alt+Tab 中显示
        Win32.AddExStyle(Handle, Win32.WS_EX_TOOLWINDOW);
        if (_noActivate) Win32.AddExStyle(Handle, Win32.WS_EX_NOACTIVATE);
        if (_clickThrough) Win32.AddExStyle(Handle, Win32.WS_EX_TRANSPARENT);

        PlaceOverVirtualScreen();
    }

    /// <summary>把窗口铺满整个虚拟屏幕（物理像素），并刷新物理像素变换。</summary>
    protected void PlaceOverVirtualScreen()
    {
        if (Handle == IntPtr.Zero) return;

        var vs = MonitorHelper.VirtualScreen;
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST,
            (int)Math.Round(vs.Left), (int)Math.Round(vs.Top),
            (int)Math.Round(vs.Width), (int)Math.Round(vs.Height),
            Win32.SWP_NOACTIVATE);

        var dpi = VisualTreeHelper.GetDpi(this);
        double s = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;

        // 让 WPF 的窗口尺寸与 SetWindowPos 设置的物理尺寸保持一致，避免布局把窗口拽回去
        Width = vs.Width / s;
        Height = vs.Height / s;

        Scene.LayoutTransform = new ScaleTransform(1.0 / s, 1.0 / s);
        Scene.Width = vs.Width;
        Scene.Height = vs.Height;
        Scene.InvalidateVisual();
    }

    /// <summary>把物理像素点转换为 Scene 内的局部坐标（同样是物理像素）。</summary>
    protected Point ToLocal(Point physical) => new(physical.X - MonitorHelper.VirtualScreen.Left,
                                                   physical.Y - MonitorHelper.VirtualScreen.Top);
}
