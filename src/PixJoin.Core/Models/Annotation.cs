using System.Windows.Media;

namespace PixJoin.Core.Models;

/// <summary>标注工具类型。</summary>
public enum AnnotationTool
{
    Rect,        // 矩形
    Ellipse,     // 椭圆
    Arrow,       // 箭头
    Pen,         // 画笔（自由绘制）
    Text,        // 文字
    Highlight,   // 高亮（半透明填充）
    Mosaic,      // 马赛克（区域像素化）
    Number,      // 序号（递增圆圈数字）
    Spotlight,   // 聚光灯（弱化周围区域，突出中间，可圆/方）
    Magnifier,   // 放大镜（区域内容放大显示，可圆/方）
    Line,        // 直线（两点）
    Curve,       // 波浪线（两点，正弦）
    Polyline,    // 折线（多点）
}

/// <summary>
/// 一条标注。坐标一律为「图片物理像素」：绘制时由 DIP 按显示缩放比换算，固化时按物理像素渲染，
/// 保证贴图缩放后标注仍精确、导出不降质。
/// </summary>
public sealed class Annotation
{
    public AnnotationTool Tool { get; init; }

    /// <summary>矩形 / 椭圆 / 高亮 / 马赛克的包围盒（物理像素）。</summary>
    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; }
    public double H { get; init; }

    /// <summary>
    /// 点列（物理像素，扁平 x,y 序列）：
    /// 画笔 = 自由点列；箭头 = 恰好 3 点 [起点, 弯曲控制点, 终点]（二次贝塞尔）；
    /// 直线 / 波浪线 = 恰好 2 点 [起点, 终点]；折线 = 多点。
    /// </summary>
    public double[]? Points { get; init; }

    /// <summary>矩形圆角半径（物理像素）。仅矩形工具使用。</summary>
    public double CornerRadius { get; init; }

    /// <summary>文字 / 序号内容。</summary>
    public string? Text { get; init; }

    public int Number { get; init; }

    public Color Color { get; init; }

    /// <summary>线宽（物理像素）。</summary>
    public double Thickness { get; init; }

    /// <summary>是否虚线（矩形 / 椭圆 / 箭头 / 画笔）。默认实线。</summary>
    public bool Dashed { get; init; }

    /// <summary>聚光灯形状：true=圆形，false=矩形。</summary>
    public bool SpotlightRound { get; init; }

    /// <summary>放大镜放大倍数（默认 2 倍）。</summary>
    public double Zoom { get; init; } = 2;

    /// <summary>放大镜形状：true=圆形镜头，false=矩形。</summary>
    public bool MagnifierRound { get; init; } = true;

    /// <summary>箭头形状（仅箭头工具有效）。默认实心三角。</summary>
    public ArrowStyle Arrow { get; init; } = ArrowStyle.Solid;
}

/// <summary>箭头头部形状。</summary>
public enum ArrowStyle
{
    Solid,   // 实心三角（默认）
    Line,    // V 形线条
    Double,  // 双箭头（两端都有头）
}
