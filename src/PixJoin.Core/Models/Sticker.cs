using System.Windows.Media.Imaging;

namespace PixJoin.Core.Models;

/// <summary>
/// 一张桌面贴图。
/// 关键约定：X / Y / W / H 一律使用「物理像素」坐标系（虚拟屏幕左上角为原点），
/// 与显示器 DPI 无关。窗口层负责在定位时按所在显示器缩放比换算回 WPF 的 DIP。
/// 这样导出的包围盒可以直接当作画布像素尺寸，保证与屏幕所见 1:1 对齐。
/// </summary>
public sealed class Sticker
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>原始位图（始终保留原始像素，显示缩放不影响它）。</summary>
    public BitmapSource Image { get; set; } = null!;

    public double X { get; set; }

    public double Y { get; set; }

    public double W { get; set; }

    public double H { get; set; }

    /// <summary>仅影响显示，不参与导出像素计算。</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>null = 独立贴图；非 null = 属于某个组合体。</summary>
    public string? GroupId { get; set; }

    public DateTime CreatedAt { get; init; } = DateTime.Now;

    /// <summary>轴对齐包围盒（物理像素）。</summary>
    public Rect Bounds => new(X, Y, W, H);

    public bool IsGrouped => GroupId is not null;

    public Sticker Clone() => (Sticker)MemberwiseClone();
}
