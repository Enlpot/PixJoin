namespace PixJoin.Core.Models;

/// <summary>
/// 贴图几何计算（纯数学，可单测）。所有坐标与尺寸均为「物理像素」。
/// 与 WPF / DPI 无关，WPF 侧负责把物理像素换算成 DIP 再交给窗口。
/// </summary>
public static class StickerGeometry
{
    /// <summary>
    /// 四角手柄缩放：根据拖拽增量计算新的宽高。
    /// <paramref name="keepAspect"/> 为 true（默认）时锁定原始宽高比（等比缩放）；
    /// 为 false 时自由拉伸。返回的宽高未做最小值钳制，由调用方钳制。
    /// </summary>
    /// <param name="oldW">缩放前宽度（物理像素）。</param>
    /// <param name="oldH">缩放前高度（物理像素）。</param>
    /// <param name="dx">水平方向的物理像素位移。右侧手柄为正，左侧手柄为负。</param>
    /// <param name="dy">垂直方向的物理像素位移。下侧手柄为正，上侧手柄为负。</param>
    /// <param name="left">是否为左侧手柄（影响水平方向符号）。</param>
    /// <param name="top">是否为上侧手柄（影响垂直方向符号）。</param>
    /// <param name="keepAspect">是否锁定宽高比。</param>
    public static (double Width, double Height) ComputeResize(
        double oldW, double oldH, double dx, double dy,
        bool left, bool top, bool keepAspect)
    {
        double signX = left ? -1 : 1;
        double signY = top ? -1 : 1;

        // 自由尺寸（未锁比例）：角沿手柄正方向跟随鼠标位移
        double nw = oldW + signX * dx;
        double nh = oldH + signY * dy;

        if (keepAspect && oldW > 0 && oldH > 0 && nw > 0 && nh > 0)
        {
            // 投影式等比：把「角的目标位置」投影到过固定对角的等比线（x/y = ratio）上。
            // 与旧「主导轴」方案不同，鼠标朝任意方向移动都连续平滑，
            // 不会在主轴切换处出现突然变大/变小，也不会放大非主导轴位移。
            double ratio = oldW / oldH;
            double t = (nw * ratio + nh) / (ratio * ratio + 1);
            nw = t * ratio;
            nh = t;
        }

        return (nw, nh);
    }
}
